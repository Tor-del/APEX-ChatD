// SPDX-License-Identifier: LGPL-3.0-or-later
// Protocol adapted from rany2/edge-tts; see THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenReader.Speech;

namespace ScreenReader.Edge;

public sealed class EdgeSpeechClient : ISpeechProvider, IDisposable
{
    private const string LegacyHost = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string ModernHost = "api.msedgeservices.com/tts/cognitiveservices";
    private const string ModernVersion = "155.0.4283.45";
    private const string ModernKey = "Ocp-Apim-Subscription-Key";
    private const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private TimeSpan clockOffset;
    private string Query(string key, string version) =>
        $"{key}={EdgeToken.TrustedClientToken}&Sec-MS-GEC={EdgeToken.Generate(DateTimeOffset.UtcNow + clockOffset)}&Sec-MS-GEC-Version=2-{version}";

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetVoicesAsync(LegacyHost, "trustedclienttoken", EdgeToken.ChromiumVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && CanFailOver(ex))
        {
            return await GetVoicesAsync(ModernHost, ModernKey, ModernVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string host, string key, string version, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/voices/list?{Query(key, version)}");
            message.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent(version));
            message.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            message.Headers.TryAddWithoutValidation("Cookie", $"muid={Guid.NewGuid():N};");
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && response.Headers.Date is { } serverTime)
            {
                var offset = serverTime - DateTimeOffset.UtcNow;
                if ((offset - clockOffset).Duration() >= TimeSpan.FromSeconds(2))
                {
                    clockOffset = offset;
                    continue;
                }
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.EnumerateArray().Select(v => new SpeechVoice(
                v.GetProperty("ShortName").GetString()!,
                v.TryGetProperty("FriendlyName",out var n) ? n.GetString()! : v.GetProperty("ShortName").GetString()!,
                v.GetProperty("Locale").GetString()!,v.GetProperty("Gender").GetString()!)).ToArray();
        }
    }

    private static bool CanFailOver(Exception error) => error switch
    {
        OperationCanceledException => true,
        HttpRequestException request => request.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone
            || (int)request.StatusCode >= 500,
        _ => false
    };

    private static string BrowserAgent(string version)
    {
        var major = version[..version.IndexOf('.')];
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0";
    }

    private async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAsync(ModernHost + "/websocket/v1", ModernKey, ModernVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && CanFailOver(ex))
        {
            return await ConnectAsync(LegacyHost + "/edge/v1", "TrustedClientToken", EdgeToken.ChromiumVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(string endpoint, string key, string version, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("User-Agent", BrowserAgent(version));
            socket.Options.SetRequestHeader("Origin", Origin);
            socket.Options.SetRequestHeader("Pragma", "no-cache");
            socket.Options.SetRequestHeader("Cache-Control", "no-cache");
            socket.Options.SetRequestHeader("Cookie", $"muid={Guid.NewGuid():N};");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(new Uri($"wss://{endpoint}?{Query(key, version)}&ConnectionId={Guid.NewGuid():N}"), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var status = (int)socket.HttpStatusCode > 0 ? socket.HttpStatusCode : (HttpStatusCode?)null;
            socket.Dispose();
            throw new HttpRequestException("Edge TTS connection failed.", ex, status);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static string DescribeFailure(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException or WebSocketException => SpeechText.Get("edge.network", "не удалось установить соединение с Edge TTS", "could not connect to Edge TTS"),
        OperationCanceledException => SpeechText.Get("edge.timeout", "время ожидания ответа Edge TTS истекло", "the Edge TTS request timed out"),
        _ => error.Message
    };
    public IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,CancellationToken cancellationToken) =>
        SpeechStreamRetry.Run(ct=>StreamWithoutRetryAsync(request,ct),request.Voice,cancellationToken);

    private async IAsyncEnumerable<byte[]> StreamWithoutRetryAsync(SpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(request.Voice,@"^[a-zA-Z0-9-]+$") || !Regex.IsMatch(request.Language,@"^[a-zA-Z0-9-]+$"))
            throw new ArgumentException(SpeechText.Get("edge.language","Некорректное имя голоса или языка.","Invalid voice or language name."));
        if (request.RatePercent is < -50 or > 100) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Text.Length > 30000) throw new ArgumentException(SpeechText.Get("edge.limit","Слишком большой фрагмент: максимум 30 000 символов.","The text exceeds the 30,000 character limit."));
        foreach (string part in SplitText(request.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var socket = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            string timestamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",CultureInfo.InvariantCulture);
            string config = "X-Timestamp:"+timestamp+"\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"+
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
            string rate = request.RatePercent.ToString("+0;-0;+0",CultureInfo.InvariantCulture)+"%";
            // Match Edge's envelope locale; the selected voice controls spoken language.
            string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><voice name='{SecurityElement.Escape(ToServiceVoiceName(request.Voice))}'><prosody pitch='+0Hz' rate='{rate}' volume='+0%'>{SecurityElement.Escape(part)}</prosody></voice></speak>";
            await SendAsync(socket,config,cancellationToken).ConfigureAwait(false);
            await SendAsync(socket,$"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{timestamp}Z\r\nPath:ssml\r\n\r\n{ssml}",cancellationToken).ConfigureAwait(false);
            bool audioReceived = false, turnEnded = false;
            byte[] buffer = new byte[16384];
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (!turnEnded)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),idleTimeout.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException(SpeechText.Get("edge.closed","Edge TTS закрыл соединение до завершения речи.","Edge TTS closed the connection before speech finished."));
                    frame.Write(buffer,0,received.Count);
                    if (frame.Length > 1048576) throw new InvalidDataException(SpeechText.Get("edge.largeResponse","Слишком большой ответ Edge TTS.","The Edge TTS response is too large."));
                } while (!received.EndOfMessage);
                byte[] data = frame.ToArray();
                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    byte[] audio = ParseAudioFrame(data);
                    if (audio.Length > 0) { audioReceived = true; yield return audio; }
                }
                else
                {
                    string headers = Encoding.UTF8.GetString(data).Split("\r\n\r\n",2)[0];
                    turnEnded = headers.Split("\r\n").Any(h => h.Equals("Path:turn.end",StringComparison.OrdinalIgnoreCase));
                }
            }
            if (!audioReceived) throw new IOException(SpeechText.Get("edge.empty","Edge TTS не вернул аудио. Проверьте голос и подключение к интернету.","Edge TTS returned no audio. Check the voice and internet connection."));
        }
    }
    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),WebSocketMessageType.Text,true,ct);

    public static string ToServiceVoiceName(string shortName)
    {
        var match=Regex.Match(shortName,@"^([a-z]{2,})-([A-Z]{2,})-(.+Neural)$");
        if (!match.Success) throw new ArgumentException(SpeechText.Get("edge.voice","Некорректное имя голоса.","Invalid voice name."));
        string locale=match.Groups[1].Value+"-"+match.Groups[2].Value;
        string name=match.Groups[3].Value;
        int separator=name.IndexOf('-');
        if (separator>=0) { locale+="-"+name[..separator]; name=name[(separator+1)..]; }
        return $"Microsoft Server Speech Text to Speech Voice ({locale}, {name})";
    }

    public static byte[] ParseAudioFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) throw new InvalidDataException(SpeechText.Get("edge.headerLength","Нет длины заголовка Edge TTS.","Missing Edge TTS header length."));
        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(frame);
        if (headerLength > frame.Length-2) throw new InvalidDataException(SpeechText.Get("edge.header","Повреждённый заголовок Edge TTS.","Invalid Edge TTS header."));
        string headers = Encoding.UTF8.GetString(frame.Slice(2,headerLength));
        if (!headers.Split("\r\n").Any(h => h.Equals("Path:audio",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(SpeechText.Get("edge.message","Неожиданный тип бинарного сообщения Edge TTS.","Unexpected Edge TTS binary message type."));
        return frame[(2+headerLength)..].ToArray();
    }

    public static IEnumerable<string> SplitText(string text)
    {
        // Count escaped UTF-8 bytes; never cut inside a Unicode scalar or XML entity.
        var part = new StringBuilder(); int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 32 && rune.Value is not (9 or 10 or 13)) continue;
            string value = rune.ToString();
            int size = Encoding.UTF8.GetByteCount(SecurityElement.Escape(value)!);
            if (bytes+size > 3000)
            {
                string pending=part.ToString(); int split=pending.Length;
                for (int i=pending.Length-1; i>pending.Length/2; i--)
                    if (char.IsWhiteSpace(pending[i])) { split=i+1; break; }
                if (!string.IsNullOrWhiteSpace(pending[..split])) yield return pending[..split];
                part.Clear(); part.Append(pending[split..]);
                bytes=Encoding.UTF8.GetByteCount(SecurityElement.Escape(part.ToString())!);
            }
            part.Append(value); bytes += size;
        }
        if (!string.IsNullOrWhiteSpace(part.ToString())) yield return part.ToString();
    }
    public void Dispose() => http.Dispose();
}
