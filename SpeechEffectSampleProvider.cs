using NAudio.Dsp;
using NAudio.Wave;

namespace TwitchChatAgent;

internal enum SpeechEffect { None, Underwater, Radio, VoiceMail, Reciver, UnderwaterShallow, UnderwaterDeeper, ClubDownstairs, OnlyTheTwitter }

internal sealed class SpeechEffectSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float mix;
    private readonly bool radio;
    private readonly bool radioNoise;
    private readonly BiQuadFilter[] lowPass;
    private readonly BiQuadFilter[] highPass;
    private readonly int modulationPeriod;
    private int channel;
    private int frame;
    private float modulation = 1;
    private float noise;

    private SpeechEffectSampleProvider(ISampleProvider source, SpeechEffect effect, float mix, bool radioNoise)
    {
        this.source = source;
        this.mix = mix;
        radio = effect == SpeechEffect.Radio;
        this.radioNoise = radio && radioNoise;
        var rate = WaveFormat.SampleRate;
        modulationPeriod = Math.Max(1, rate * 2 / 3);
        // Keep both filters below Nyquist, including for low-rate speech files.
        var cutoff = Math.Min(radio ? 3000f : 950f, rate * 0.45f);
        lowPass = new BiQuadFilter[WaveFormat.Channels];
        highPass = radio ? new BiQuadFilter[WaveFormat.Channels] : [];
        for (var index = 0; index < lowPass.Length; index++)
        {
            lowPass[index] = BiQuadFilter.LowPassFilter(rate, cutoff, 0.707f);
            if (radio)
                highPass[index] = BiQuadFilter.HighPassFilter(rate, Math.Min(350f, cutoff * 0.5f), 0.707f);
        }
    }

    public static ISampleProvider Apply(ISampleProvider source, SpeechEffect effect, int intensity, bool radioNoise)
    {
        var mix = Math.Clamp(intensity, 0, 100) / 100f;
        return mix > 0 && effect is SpeechEffect.Underwater or SpeechEffect.Radio
            ? new SpeechEffectSampleProvider(source, effect, mix, radioNoise)
            : source;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        for (var index = offset; index < offset + read; index++)
        {
            if (channel == 0)
            {
                if (!radio)
                {
                    modulation = 0.94f + 0.06f * MathF.Sin(MathF.Tau * frame / modulationPeriod);
                    if (++frame == modulationPeriod)
                        frame = 0;
                }
                if (radioNoise)
                    noise = (Random.Shared.NextSingle() * 2 - 1) * 0.012f;
            }

            var dry = buffer[index];
            var wet = radio
                ? MathF.Tanh(lowPass[channel].Transform(highPass[channel].Transform(dry)) * 1.6f) * 0.8f + noise
                : lowPass[channel].Transform(dry) * modulation;
            buffer[index] = dry * (1 - mix) + wet * mix;
            if (++channel == WaveFormat.Channels)
                channel = 0;
        }
        return read;
    }
}
