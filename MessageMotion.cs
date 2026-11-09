namespace TwitchChatAgent;

internal enum MessageEasing { Linear, EaseOut, EaseIn, EaseInOut }

internal sealed record MotionOptions(int DurationMs = 280, int DelayMs = 0,
    MessageEasing Easing = MessageEasing.EaseOut, int Opacity = 0,
    int OffsetX = 0, int OffsetY = 0, int Scale = 92, int OriginX = 0, int OriginY = 50)
{
    public static MotionOptions Default(MessageAnimation kind, int duration = 280) => kind switch
    {
        MessageAnimation.Rise => new(DurationMs: duration, OffsetY: 14),
        MessageAnimation.Slide => new(DurationMs: duration, OffsetX: 24),
        _ => new(DurationMs: duration)
    };
}

internal sealed record MotionProfiles(MotionOptions? Fade = null, MotionOptions? Rise = null,
    MotionOptions? Slide = null, MotionOptions? Zoom = null)
{
    public MotionOptions Get(MessageAnimation kind, int duration) =>
        (kind switch
        {
            MessageAnimation.Fade => Fade,
            MessageAnimation.Rise => Rise,
            MessageAnimation.Slide => Slide,
            MessageAnimation.Zoom => Zoom,
            _ => null
        }) ?? MotionOptions.Default(kind, duration);

    public MotionProfiles Set(MessageAnimation kind, MotionOptions options) => kind switch
    {
        MessageAnimation.Fade => this with { Fade = options },
        MessageAnimation.Rise => this with { Rise = options },
        MessageAnimation.Slide => this with { Slide = options },
        MessageAnimation.Zoom => this with { Zoom = options },
        _ => this
    };
}
