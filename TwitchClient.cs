using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchChatAgent;

internal sealed record ChatMessage(string Name, string Text,
    [property: JsonIgnore] string? Id = null,
    [property: JsonIgnore] string? UserId = null,
    [property: JsonIgnore] string? ChannelId = null,
    [property: JsonIgnore] bool IsModerator = false, string? NameColor = null)
{
    public ChatMessagePart[] Parts { get; init; } = [];
    public Guid DisplayId { get; } = Guid.NewGuid();
    public long DisplayTimeMs => ReceivedAt.ToUnixTimeMilliseconds();
    internal DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record TwitchModeration(string ClientId, string Token, string ChannelId, bool CanDelete)
{
    public bool Allows(ChatMessage message) => BlockedReason(message) is null;

    public string? BlockedReason(ChatMessage message)
    {
        if (message.ChannelId != ChannelId)
            return Ui.T("Сообщение не относится к подключённому каналу.");
        if (string.IsNullOrEmpty(message.UserId) || string.IsNullOrEmpty(message.Id))
            return Ui.T("Для этого сообщения нет идентификатора Twitch.");
        if (message.UserId == ChannelId)
            return Ui.T("Это сообщение владельца канала.");
        if (message.IsModerator)
            return Ui.T("Это сообщение модератора канала.");
        return CanDelete ? null : Ui.T("Не хватает прав. Нажми «Обновить права Twitch».");
    }
}
internal sealed record TwitchTokens(
    [property: JsonPropertyName("access_token")] string Access,
    [property: JsonPropertyName("refresh_token")] string Refresh);

internal sealed class TwitchClient
{
    public const string DefaultClientId = "l2hoowsobx1u60bn5ax6qht8hutclo";
    private const string Scope = "user:read:chat";
    private const string DeleteScope = "moderator:manage:chat_messages";
    private const string Identity = "https://id.twitch.tv/oauth2/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Uri Gateway = new("wss://eventsub.wss.twitch.tv/ws");

    public static async Task<TwitchTokens> SignInAsync(string clientId, Action<string> status, CancellationToken ct)
    {
        var scopes = $"{Scope} {DeleteScope}";
        using var response = await PostAsync("device", ct, ("client_id", clientId), ("scopes", scopes));
        response.EnsureSuccessStatusCode();
        var device = await ReadAsync(response, ct);
        var code = device.GetProperty("user_code").GetString();
        var url = new Uri(device.GetProperty("verification_uri").GetString()!);
        if (url.Scheme != "https" || url.Host != "www.twitch.tv")
            throw new InvalidDataException(Ui.T("Неожиданный адрес страницы активации Twitch."));

        status(Ui.F("Подтверди доступ в браузере. Код: {0}", code));
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(device.GetProperty("expires_in").GetInt32()));
        var pollToken = deadline.Token;
        var interval = Math.Max(1, device.GetProperty("interval").GetInt32());
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), pollToken);
            using var poll = await PostAsync("token", pollToken,
                ("client_id", clientId), ("scopes", scopes),
                ("device_code", device.GetProperty("device_code").GetString()!),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"));
            var body = await ReadAsync(poll, pollToken);
            if (poll.IsSuccessStatusCode)
                return body.Deserialize<TwitchTokens>()!;

            var error = body.GetProperty("message").GetString();
            if (error == "authorization_pending")
                continue;
            if (error == "slow_down")
            {
                interval += 5;
                continue;
            }
            throw new InvalidOperationException(Ui.F("Twitch отклонил активацию: {0}", error));
        }
    }

    public static async Task MonitorAsync(string clientId, TwitchTokens tokens, Action<TwitchTokens> save,
        Action<string> status, Action<ChatMessage> publish, Action<TwitchModeration?> setModeration, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var order = new Queue<string>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var account = await ValidateAsync(tokens.Access, ct);
                if (account is null)
                {
                    using var refreshed = await PostAsync("token", ct, ("client_id", clientId),
                        ("grant_type", "refresh_token"), ("refresh_token", tokens.Refresh));
                    if (refreshed.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                        throw new InvalidOperationException(Ui.T("Сессия Twitch истекла. Подключи аккаунт заново."));
                    refreshed.EnsureSuccessStatusCode();
                    tokens = (await ReadAsync(refreshed, ct)).Deserialize<TwitchTokens>()!;
                    save(tokens);
                    ct.ThrowIfCancellationRequested();
                    account = await ValidateAsync(tokens.Access, ct)
                        ?? throw new InvalidOperationException(Ui.T("Twitch не принял обновлённый токен."));
                }

                var identity = account.Value;
                var scopes = identity.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()).ToHashSet();
                if (identity.GetProperty("client_id").GetString() != clientId || !scopes.Contains(Scope))
                    throw new InvalidOperationException(Ui.T("Токен Twitch не соответствует настройкам приложения."));

                var channel = identity.GetProperty("user_id").GetString()!;
                var login = identity.GetProperty("login").GetString();
                var nextValidation = ValidationDeadline(identity);
                var moderation = new TwitchModeration(clientId, tokens.Access, channel,
                    scopes.Contains(DeleteScope));
                await ListenAsync(clientId, tokens.Access, channel, login, nextValidation, status, () => setModeration(moderation), (id, message) =>
                {
                    if (!seen.Add(id))
                        return;
                    order.Enqueue(id);
                    if (order.Count > 4096)
                        seen.Remove(order.Dequeue());
                    publish(message);
                }, ct);
                setModeration(null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is HttpRequestException or WebSocketException or IOException or OperationCanceledException)
            {
                setModeration(null);
                status(Ui.T("Соединение прервано. Повторное подключение через 5 секунд…"));
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static async Task ListenAsync(string clientId, string token, string channel, string? login,
        DateTimeOffset nextValidation, Action<string> status, Action connected,
        Action<string, ChatMessage> publish, CancellationToken ct)
    {
        using var initial = await ConnectAsync(Gateway, ct);
        ClientWebSocket socket = initial;
        try
        {
            var welcome = await ReceiveAsync(socket, 10, ct);
            var session = welcome.GetProperty("payload").GetProperty("session");
            var timeout = session.GetProperty("keepalive_timeout_seconds").GetInt32();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", clientId);
            request.Content = JsonContent.Create(new
            {
                type = "channel.chat.message",
                version = "1",
                condition = new { broadcaster_user_id = channel, user_id = channel },
                transport = new { method = "websocket", session_id = session.GetProperty("id").GetString() }
            });
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                throw new InvalidOperationException(Ui.F("Twitch отклонил подписку ({0}).", (int)response.StatusCode));
            response.EnsureSuccessStatusCode();
            status(Ui.F("Читаем чат twitch.tv/{0}", login));
            connected();

            while (true)
            {
                if (DateTimeOffset.UtcNow >= nextValidation)
                {
                    var account = await ValidateAsync(token, ct);
                    if (account is null)
                        return;
                    nextValidation = ValidationDeadline(account.Value);
                }

                var frame = await ReceiveAsync(socket, timeout + 3, ct);
                var metadata = frame.GetProperty("metadata");
                var payload = frame.GetProperty("payload");
                switch (metadata.GetProperty("message_type").GetString())
                {
                    case "notification":
                        var chat = payload.GetProperty("event");
                        var message = chat.GetProperty("message");
                        publish(chat.GetProperty("message_id").GetString()!,
                            new ChatMessage(chat.GetProperty("chatter_user_name").GetString()!,
                                message.GetProperty("text").GetString()!,
                                chat.GetProperty("message_id").GetString(),
                                chat.GetProperty("chatter_user_id").GetString(), channel,
                                chat.GetProperty("badges").EnumerateArray()
                                    .Any(badge => badge.GetProperty("set_id").GetString() == "moderator"),
                                chat.TryGetProperty("color", out var color) ? color.GetString() : null)
                            { Parts = ChatMessagePart.Read(message) });
                        break;
                    case "session_reconnect":
                        var uri = new Uri(payload.GetProperty("session").GetProperty("reconnect_url").GetString()!);
                        if (uri.Scheme != "wss" || uri.Host != Gateway.Host)
                            throw new IOException(Ui.T("Неожиданный адрес переподключения."));
                        var next = await ConnectAsync(uri, ct);
                        try
                        {
                            var nextWelcome = await ReceiveAsync(next, 10, ct);
                            timeout = nextWelcome.GetProperty("payload").GetProperty("session")
                                .GetProperty("keepalive_timeout_seconds").GetInt32();
                        }
                        catch
                        {
                            next.Dispose();
                            throw;
                        }
                        socket.Dispose();
                        socket = next;
                        break;
                    case "revocation":
                        throw new InvalidOperationException(Ui.T("Twitch отозвал подписку. Подключи аккаунт заново."));
                }
            }
        }
        finally
        {
            if (socket != initial)
                socket.Dispose();
        }
    }

    public static async Task<bool> IsLiveAsync(TwitchModeration session, CancellationToken ct)
    {
        var channel = Uri.EscapeDataString(session.ChannelId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={channel}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("Client-Id", session.ClientId);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await ReadAsync(response, ct);
        return body.GetProperty("data").EnumerateArray().Any(stream => stream.GetProperty("type").GetString() == "live");
    }

    public static async Task DeleteMessageAsync(TwitchModeration session, ChatMessage message, CancellationToken ct)
    {
        if (!session.Allows(message))
            throw new InvalidOperationException(Ui.T("Для этого сообщения действие недоступно."));

        var channel = Uri.EscapeDataString(session.ChannelId);
        var query = $"broadcaster_id={channel}&moderator_id={channel}";
        var endpoint = $"chat?{query}&message_id={Uri.EscapeDataString(message.Id!)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            "https://api.twitch.tv/helix/moderation/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("Client-Id", session.ClientId);

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var details = await ReadAsync(response, ct);
            var reason = details.TryGetProperty("message", out var value) ? value.GetString() : response.ReasonPhrase;
            throw new InvalidOperationException(Ui.F("Twitch отклонил действие ({0}): {1}", (int)response.StatusCode, reason));
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, deadline.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, int seconds, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 1024 * 1024)
                throw new IOException(Ui.T("Соединение Twitch закрыто или получен недопустимый кадр."));
            bytes.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        bytes.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(bytes, cancellationToken: ct);
    }

    private static DateTimeOffset ValidationDeadline(JsonElement account) =>
        DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(account.GetProperty("expires_in").GetInt32(), 1, 3540));

    private static async Task<JsonElement?> ValidateAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Identity + "validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, ct);
    }

    private static async Task<HttpResponseMessage> PostAsync(string endpoint, CancellationToken ct,
        params (string Name, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(fields.Select(field =>
            new KeyValuePair<string, string>(field.Name, field.Value)));
        return await Http.PostAsync(Identity + endpoint, content, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
}
