using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ScreenReader.Speech;

namespace TwitchChatAgent;

internal sealed record ChatTheme(string Font = "Segoe UI", int Size = 20, string Text = "#FFFFFF",
    string Name = "#BFA6FF", string Background = "#171322", int Opacity = 75, int Rows = 8, int Radius = 0, bool UseTwitchNameColor = true,
    bool HideExpiredMessages = false, int MessageTimeoutSeconds = 15, bool NewestAtTop = false,
    bool AnimateMessages = false, int MessageTransitionMs = 280, ObsMessageMotion? MessageMotion = null);

internal enum ObsMessageEffect { Slide, Scale, Fade, Particles }
[Flags]
internal enum ObsMessageEffects { None = 0, Slide = 1, Scale = 2, Fade = 4 }
internal enum ObsMoveCurve { Linear, EaseIn, EaseOut, EaseInOut }
internal sealed record ObsTransition(int DurationMs = 280, int DelayMs = 0, ObsMoveCurve Curve = ObsMoveCurve.EaseInOut,
    int OffsetX = 0, int OffsetY = 32, int Scale = 0, int OriginX = 0, int OriginY = 0, int Opacity = 0,
    ObsMessageEffects? Effects = null, ObsAnimationItem[]? Items = null, int ParticleSize = 4, int ParticleSpread = 80,
    int ParticleLimit = 1200, int ParticleDirection = 0, int ParticleCone = 360);
internal sealed record ObsAnimationItem(ObsMessageEffect Kind, bool Enabled, ObsTransition Options);
internal sealed record ObsMessageMotion(ObsMessageEffect Enter = ObsMessageEffect.Slide,
    ObsMessageEffect Exit = ObsMessageEffect.Fade, bool AnimateMove = true, int MoveDurationMs = 280,
    ObsMoveCurve MoveCurve = ObsMoveCurve.EaseInOut, ObsTransition? Entrance = null, ObsTransition? Departure = null,
    ObsTransition? Movement = null);

internal sealed record Settings
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchChatAgent");

    public string Language { get; set; } = "ru";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool SkipExitConfirmation { get; set; }
    public bool OverlayLiveOnly { get; set; }
    public bool Overlay { get; set; }
    public bool Obs { get; set; }
    public string? ObsBackgroundPath { get; set; }
    public ObsEffectSettings ObsEffects { get; set; } = new();
    public bool Speech { get; set; }
    public bool ReadName { get; set; }
    public bool Edge { get; set; }
    public bool CompatibleTts { get; set; }
    public string TtsEndpoint { get; set; } = "http://127.0.0.1:5050/v1/audio/speech";
    public string TtsModel { get; set; } = "tts-1";
    public string TtsVoice { get; set; } = "alloy";
    public SpeechAudioFormat TtsFormat { get; set; } = SpeechAudioFormat.Mp3;
    public string ProtectedTtsKey { get; set; } = "";
    public string WindowsVoice { get; set; } = "";
    public bool RetrySpeech { get; set; }
    public string AudioOutputId { get; set; } = "";
    public bool RandomEdgeVoice { get; set; }
    public bool RandomEdgeVoiceInLanguage { get; set; }
    public bool PinRandomEdgeVoice { get; set; }
    public int SpeechPitch { get; set; }
    public int SpeechFormant { get; set; }
    public QueueBindings SpeechHotkeys { get; set; } = new();
    public string? EdgeLanguage { get; set; }
    public string EdgeVoice { get; set; } = "ru-RU-SvetlanaNeural";
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
    public SpeechEffect SpeechEffect { get; set; }
    public int SpeechEffectIntensity { get; set; } = 75;
    public bool SpeechRadioNoise { get; set; }
    public ChatTheme DesktopTheme { get; set; } = new();
    public ChatTheme ObsTheme { get; set; } = new(Opacity: 0);
    public double Left { get; set; } = 32;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 300;

    public static Settings Load() => File.Exists(Path.Combine(Folder, "settings.json"))
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(Path.Combine(Folder, "settings.json"))) ?? new()
        : new();

    public void Save() => Write("settings.json", JsonSerializer.SerializeToUtf8Bytes(this));

    public string ReadTtsKey() => string.IsNullOrEmpty(ProtectedTtsKey) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedTtsKey), null, DataProtectionScope.CurrentUser));

    public void SetTtsKey(string key) => ProtectedTtsKey = string.IsNullOrWhiteSpace(key) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));

    public static TwitchTokens? LoadTokens(string clientId)
    {
        var path = Path.Combine(Folder, "twitch.dat");
        if (!File.Exists(path))
            return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        var saved = JsonSerializer.Deserialize<SavedSession>(bytes);
        return saved?.ClientId == clientId ? saved.Tokens : null;
    }

    public static void SaveTokens(string clientId, TwitchTokens tokens) =>
        Write("twitch.dat", ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new SavedSession(clientId, tokens)),
            null, DataProtectionScope.CurrentUser));

    public static void ClearTokens() => File.Delete(Path.Combine(Folder, "twitch.dat"));

    private static void Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed record SavedSession(string ClientId, TwitchTokens Tokens);
}
