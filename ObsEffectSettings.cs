namespace TwitchChatAgent;

internal enum EffectTarget { Chat, Background, Both }
internal enum GlitchTrigger { NewMessage, Random }

internal sealed record ChromaticSettings(bool Enabled = false, EffectTarget Target = EffectTarget.Chat,
    int OffsetX = 3, int OffsetY = 1, bool Radial = false, int Modulation = 15, int Intensity = 100);

internal sealed record GlitchSettings(bool Enabled = true, EffectTarget Target = EffectTarget.Chat,
    GlitchTrigger Trigger = GlitchTrigger.NewMessage, int Weak = 30, int Strong = 80, int Threshold = 85,
    int MinDuration = 120, int MaxDuration = 260, int MinInterval = 1800, int MaxInterval = 3500,
    int Columns = 5, int NoiseSize = 64, int ColorShift = 3);

internal sealed record ScanlineSettings(bool Enabled = false, EffectTarget Target = EffectTarget.Background,
    int Density = 125, int Intensity = 25, int Speed = 0);

internal sealed record ObsEffectSettings
{
    public bool Enabled { get; init; }
    public ChromaticSettings Chromatic { get; init; } = new();
    public GlitchSettings Glitch { get; init; } = new();
    public ScanlineSettings Scanlines { get; init; } = new();
}
