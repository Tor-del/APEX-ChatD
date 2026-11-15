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
    private readonly TaskCompletionSource refreshRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task RefreshRequested => refreshRequested.Task;
    internal void RequestRefresh() => refreshRequested.TrySetResult();

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

internal sealed class TwitchSignInRequiredException(string message) : Exception(message);

internal sealed class TwitchClient
{
    public const string DefaultClientId = "l2hoowsobx1u60bn5ax6qht8hutclo";
    private const string Scope = "user:read:chat";
    private const string DeleteScope = "moderator:manage:chat_messages";
    private const string Identity = "https://id.twitch.tv/oauth2/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Uri Gateway = new("wss://eventsub.wss.twitch.tv/ws");
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const string ChatMessageEvent = "channel.chat.message";
    private const string ChatDeleteEvent = "channel.chat.message_delete";
    private const string ChatClearUserEvent = "channel.chat.clear_user_messages";
    private const string ChatClearEvent = "channel.chat.clear";

    private sealed record Account(TwitchModeration Session, string? Login, DateTimeOffset ExpiresAt)
    {
        public DateTimeOffset NextCheck
        {
            get
            {
                var hourlyCheck = DateTimeOffset.UtcNow.AddMinutes(59);
                var refreshAt = ExpiresAt - RefreshLeadTime;
                return refreshAt < hourlyCheck ? refreshAt : hourlyCheck;
            }
        }
    }

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
        Action<string> status, Action<ChatMessage> publish, Action<Func<ChatMessage, bool>> remove,
        Action<TwitchModeration?> setModeration, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var order = new Queue<string>();
        var forceRefresh = false;
        var awaitingValidation = false;
        var validationRejections = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var account = await AuthorizeAsync(forceRefresh);
                await ListenAsync(account, AuthorizeAsync, status, setModeration, HandleNotification, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is HttpRequestException or WebSocketException or IOException or OperationCanceledException or JsonException)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                    forceRefresh = true;
                setModeration(null);
                status(Ui.T("Соединение прервано. Повторное подключение через 5 секунд…"));
                await Task.Delay(RetryDelay, ct);
            }
        }

        async Task<Account> AuthorizeAsync(bool refresh)
        {
            // A successful exchange consumes the old refresh token. Confirm the new
            // access token before considering another exchange, even after reconnecting.
            if (awaitingValidation)
                return await ConfirmRefreshAsync();

            var account = refresh ? null : await GetAccountAsync(clientId, tokens.Access, ct);
            if (account is not null && account.ExpiresAt - DateTimeOffset.UtcNow > RefreshLeadTime)
                return account;

            forceRefresh = true;
            if (string.IsNullOrWhiteSpace(tokens.Refresh))
                throw SignInRequired();
            using var response = await PostAsync("token", ct, ("client_id", clientId),
                ("grant_type", "refresh_token"), ("refresh_token", tokens.Refresh));
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw SignInRequired();
            response.EnsureSuccessStatusCode();
            var renewed = (await ReadAsync(response, ct)).Deserialize<TwitchTokens>();
            if (renewed is null || string.IsNullOrWhiteSpace(renewed.Access) || string.IsNullOrWhiteSpace(renewed.Refresh))
                throw new IOException(Ui.T("Twitch вернул неполные данные для входа."));
            ct.ThrowIfCancellationRequested();
            tokens = renewed;
            save(tokens);
            forceRefresh = false;
            awaitingValidation = true;
            validationRejections = 0;
            return await ConfirmRefreshAsync();
        }

        async Task<Account> ConfirmRefreshAsync()
        {
            var account = await GetAccountAsync(clientId, tokens.Access, ct);
            if (account is null)
            {
                if (++validationRejections >= 3)
                    throw new TwitchSignInRequiredException(Ui.T("Twitch не принял обновлённый токен."));
                throw new IOException(Ui.T("Twitch не принял обновлённый токен."));
            }
            awaitingValidation = false;
            forceRefresh = false;
            return account;
        }

        bool Remember(string id)
        {
            if (!seen.Add(id))
                return false;
            order.Enqueue(id);
            if (order.Count > 4096)
                seen.Remove(order.Dequeue());
            return true;
        }

        void HandleNotification(string channel, string type, string eventId, JsonElement chat)
        {
            switch (type)
            {
                case ChatMessageEvent when chat.GetProperty("message_id").GetString() is { Length: > 0 } id:
                    if (!Remember("chat:" + id)) break;
                    var message = chat.GetProperty("message");
                    publish(new ChatMessage(chat.GetProperty("chatter_user_name").GetString()!,
                        message.GetProperty("text").GetString()!, id,
                        chat.GetProperty("chatter_user_id").GetString(), channel,
                        chat.GetProperty("badges").EnumerateArray()
                            .Any(badge => badge.GetProperty("set_id").GetString() == "moderator"),
                        chat.TryGetProperty("color", out var color) ? color.GetString() : null)
                    { Parts = ChatMessagePart.Read(message) });
                    break;
                case ChatDeleteEvent when chat.GetProperty("message_id").GetString() is { Length: > 0 } deletedId:
                    Remember("chat:" + deletedId);
                    remove(message => message.ChannelId == channel && message.Id == deletedId);
                    break;
                case ChatClearUserEvent when chat.GetProperty("target_user_id").GetString() is { Length: > 0 } userId:
                    if (Remember("event:" + eventId))
                        remove(message => message.ChannelId == channel && message.UserId == userId);
                    break;
                case ChatClearEvent:
                    if (Remember("event:" + eventId))
                        remove(message => message.ChannelId == channel);
                    break;
            }
        }
    }

    private static async Task ListenAsync(Account account, Func<bool, Task<Account>> authorize,
        Action<string> status, Action<TwitchModeration?> setModeration,
        Action<string, string, string, JsonElement> notify, CancellationToken ct)
    {
        using var initial = await ConnectAsync(Gateway, ct);
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ClientWebSocket socket = initial;
        Task<JsonElement>? receive = null;
        try
        {
            var welcome = await ReceiveAsync(socket, 10, ct);
            var session = welcome.GetProperty("payload").GetProperty("session");
            var timeout = session.GetProperty("keepalive_timeout_seconds").GetInt32();
            var moderation = account.Session;
            var sessionId = session.GetProperty("id").GetString()!;
            await SubscribeAsync(moderation, sessionId, ChatMessageEvent, ct);
            await Task.WhenAll(
                SubscribeAsync(moderation, sessionId, ChatDeleteEvent, ct),
                SubscribeAsync(moderation, sessionId, ChatClearUserEvent, ct),
                SubscribeAsync(moderation, sessionId, ChatClearEvent, ct));
            status(Ui.F("Читаем чат twitch.tv/{0}", account.Login));
            setModeration(moderation);
            var nextCheck = DelayUntil(account.NextCheck, listening.Token);
            Task refreshRequested = moderation.RefreshRequested;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                receive ??= ReceiveAsync(socket, timeout + 3, listening.Token);
                await Task.WhenAny(receive, nextCheck, refreshRequested);
                if (nextCheck.IsCompleted || refreshRequested.IsCompleted)
                {
                    try
                    {
                        var refresh = moderation.RefreshRequested.IsCompleted;
                        account = await authorize(refresh);
                        if (refresh ||
                            account.Session.Token != moderation.Token ||
                            account.Session.ChannelId != moderation.ChannelId ||
                            account.Session.CanDelete != moderation.CanDelete)
                        {
                            moderation = account.Session;
                            setModeration(moderation);
                        }
                        nextCheck = DelayUntil(account.NextCheck, listening.Token);
                        refreshRequested = moderation.RefreshRequested;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested &&
                        DateTimeOffset.UtcNow < account.ExpiresAt &&
                        ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                    {
                        // Keep the chat open and coalesce further refresh requests until the retry.
                        nextCheck = Task.Delay(RetryDelay, listening.Token);
                        refreshRequested = nextCheck;
                    }
                }

                if (!receive.IsCompleted)
                    continue;
                var frame = await receive;
                receive = null;
                var metadata = frame.GetProperty("metadata");
                var payload = frame.GetProperty("payload");
                switch (metadata.GetProperty("message_type").GetString())
                {
                    case "notification":
                        notify(moderation.ChannelId, metadata.GetProperty("subscription_type").GetString()!,
                            metadata.GetProperty("message_id").GetString()!, payload.GetProperty("event"));
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
                        if (payload.GetProperty("subscription").GetProperty("status").GetString() == "authorization_revoked")
                            // The subscription may have been created with an older access token.
                            // Reopen it after validating the current token.
                            throw new IOException();
                        throw new InvalidOperationException(Ui.T("Twitch отозвал подписку. Подключи аккаунт заново."));
                }
            }
        }
        finally
        {
            listening.Cancel();
            if (socket != initial)
                socket.Dispose();
            if (receive is not null)
                await ((Task)receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static Task DelayUntil(DateTimeOffset time, CancellationToken ct)
    {
        var delay = time - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? Task.Delay(delay, ct) : Task.CompletedTask;
    }

    private static async Task SubscribeAsync(TwitchModeration session, string sessionId, string type, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("Client-Id", session.ClientId);
        request.Content = JsonContent.Create(new
        {
            type,
            version = "1",
            condition = new { broadcaster_user_id = session.ChannelId, user_id = session.ChannelId },
            transport = new { method = "websocket", session_id = sessionId }
        });
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            throw new InvalidOperationException(Ui.F("Twitch отклонил подписку ({0}).", (int)response.StatusCode));
        response.EnsureSuccessStatusCode();
    }

    public static async Task<bool> IsLiveAsync(TwitchModeration session, CancellationToken ct)
    {
        var channel = Uri.EscapeDataString(session.ChannelId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={channel}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("Client-Id", session.ClientId);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            session.RequestRefresh();
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
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            session.RequestRefresh();
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

    private static TwitchSignInRequiredException SignInRequired() =>
        new(Ui.T("Сессия Twitch истекла. Подключи аккаунт заново."));

    private static async Task<Account?> GetAccountAsync(string clientId, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Identity + "validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        var account = await ReadAsync(response, ct);
        var scopes = account.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()).ToHashSet();
        if (account.GetProperty("client_id").GetString() != clientId || !scopes.Contains(Scope) ||
            account.GetProperty("user_id").GetString() is not { Length: > 0 } channel)
            throw new TwitchSignInRequiredException(Ui.T("Токен Twitch не соответствует настройкам приложения."));

        return new(new TwitchModeration(clientId, token, channel, scopes.Contains(DeleteScope)),
            account.GetProperty("login").GetString(),
            DateTimeOffset.UtcNow.AddSeconds(account.GetProperty("expires_in").GetInt32()));
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
