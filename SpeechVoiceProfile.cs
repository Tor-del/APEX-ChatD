using System.Security.Cryptography;
using System.Text;
using ScreenReader.Speech;

namespace TwitchChatAgent;

internal enum SpeechProvider { Windows, Edge, Compatible }

internal sealed record SpeechVoiceProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public SpeechProvider Provider { get; init; }
    public string? PluginProviderId { get; init; }
    public string ProviderOptions { get; init; } = "";
    public const string EdgeProviderId = "apexchatd.edge-tts/edge";

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ExtensionProviderId => PluginProviderId ?? (Provider == SpeechProvider.Edge ? EdgeProviderId : null);
    public string Voice { get; init; } = "";
    public string Language { get; init; } = "";
    public string Endpoint { get; init; } = "http://127.0.0.1:5050/v1/audio/speech";
    public string Model { get; init; } = "tts-1";
    public SpeechAudioFormat Format { get; init; } = SpeechAudioFormat.Mp3;
    public string ProtectedApiKey { get; init; } = "";
    public int Rate { get; init; }
    public int Volume { get; init; } = 100;
    public SpeechEffect Effect { get; init; }
    public int Intensity { get; init; } = 75;
    public bool RadioNoise { get; init; }
    public int Pitch { get; init; }
    public int Formant { get; init; }

    public string ReadApiKey() => string.IsNullOrEmpty(ProtectedApiKey) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), null, DataProtectionScope.CurrentUser));

    public static string ProtectApiKey(string key) => string.IsNullOrWhiteSpace(key) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
}
