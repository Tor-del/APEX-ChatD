using System.Security.Cryptography;
using System.Text.Json;

namespace TwitchChatAgent;

internal sealed record ChatTheme(string Font = "Segoe UI", int Size = 20, string Text = "#FFFFFF",
    string Name = "#BFA6FF", string Background = "#171322", int Opacity = 75, int Rows = 8, int Radius = 0, bool UseTwitchNameColor = true,
    bool HideExpiredMessages = false, int MessageTimeoutSeconds = 15, bool NewestAtTop = false,
    bool AnimateMessages = false, int MessageTransitionMs = 280, ObsMessageMotion? MessageMotion = null,
    ObsMessageStyle? MessageStyle = null);

internal sealed record ObsMessageStyle(bool Enabled = false, string Background = "#CC171322", int Radius = 8,
    int Padding = 8, string BorderColor = "#BFA6FF", int BorderWidth = 0);

internal sealed record SpeechPrefixRule(bool Enabled = true, string Prefix = "!", bool Inverted = true);

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
    public bool AutoCheckUpdates { get; set; } = true;
    public string? IgnoredUpdateVersion { get; set; }
    public string? DeferredUpdateVersion { get; set; }
    public DateTimeOffset? UpdateReminderAfter { get; set; }
    public bool OverlayLiveOnly { get; set; }
    public bool Overlay { get; set; }
    public bool Obs { get; set; }
    public string? ObsBackgroundPath { get; set; }
    public ObsEffectSettings ObsEffects { get; set; } = new();
    public bool Speech { get; set; }
    public bool ReadName { get; set; }
    public string ReadNamePhrase { get; set; } = "";
    public SpeechPrefixRule SpeechFilter { get; set; } = new();
    public bool SkipSpeechEmotes { get; set; }
    public SpeechVoiceProfile[] SpeechVoices { get; set; } = [];
    public Guid? PrimarySpeechVoiceId { get; set; }
    public bool RetrySpeech { get; set; }
    public string AudioOutputId { get; set; } = "";
    public bool RandomSpeechVoice { get; set; }
    public bool PinSpeechVoice { get; set; }
    public QueueBindings SpeechHotkeys { get; set; } = new();
    public Dictionary<string, QueueShortcut> PluginHotkeys { get; set; } = new(StringComparer.Ordinal);
    public int Volume { get; set; } = 100;
    public ChatTheme DesktopTheme { get; set; } = new();
    public ChatTheme ObsTheme { get; set; } = new(Opacity: 0);
    public double Left { get; set; } = 32;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 300;

    public static Settings Load()
    {
        var path = Path.Combine(Folder, "settings.json");
        if (!File.Exists(path))
            return new();

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var settings = root.Deserialize<Settings>() ?? new();
        settings.PluginHotkeys ??= new(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty(nameof(SpeechFilter), out _))
            return settings;

        var hadPrefixFilter = root.TryGetProperty("FilterSpeechByPrefix", out var filter) && filter.ValueKind == JsonValueKind.True;
        var skippedCommands = !root.TryGetProperty("SkipSpeechCommands", out var skip) || skip.ValueKind != JsonValueKind.False;
        var prefix = root.TryGetProperty("SpeechPrefix", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        settings.SpeechFilter = hadPrefixFilter ? new(true, prefix, false)
            : skippedCommands ? new() : new(false, prefix, false);
        return settings;
    }

    public void Save() => Write("settings.json", JsonSerializer.SerializeToUtf8Bytes(this));

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
