using NAudio.Dsp;
using NAudio.Wave;

namespace TwitchChatAgent;

internal static class SpeechVoiceTransform
{
    private const int FormantOrder = 11;
    private const int PitchFrame = 2048;
    private const int PitchOverlap = 8;
    private static readonly SpeechSpectralProcessor FormantProcessor = new(FormantOrder);

    public static ISampleProvider Apply(ISampleProvider source, int pitch, int formant, CancellationToken ct)
    {
        pitch = Math.Clamp(pitch, -12, 12);
        formant = Math.Clamp(formant, -6, 6);
        if (pitch == 0 && formant == 0) return source;

        // The pitch shifter moves the whole spectrum. Counter-shift its envelope so the two controls stay independent.
        if (formant != pitch)
            source = ShiftEnvelope(source, Math.Pow(2, (formant - pitch) / 12.0), ct);
        return pitch == 0 ? Limit(source, ct) : ShiftPitch(source, (float)Math.Pow(2, pitch / 12.0), ct);
    }

    private static ISampleProvider ShiftEnvelope(ISampleProvider source, double ratio, CancellationToken ct)
    {
        var size = 1 << FormantOrder;
        var count = size / 2 + 1;
        var radius = Math.Clamp((int)Math.Round(160.0 * size / source.WaveFormat.SampleRate), 1, count - 1);
        var power = new double[count];
        var envelope = new double[count];
        var prefix = new double[count + 1];
        return FormantProcessor.Apply(source, spectrum =>
        {
            var peak = 0d;
            for (var i = 0; i < count; i++)
            {
                var bin = spectrum[i];
                power[i] = (double)bin.X * bin.X + (double)bin.Y * bin.Y;
                peak = Math.Max(peak, power[i]);
            }
            if (peak < 1e-16) return;

            // Smooth across nearby harmonics, then move the spectral envelope while leaving the bins in place.
            Smooth(power, envelope, prefix, radius);
            Smooth(envelope, power, prefix, radius);
            var floor = peak * 1e-6;
            for (var i = 0; i < count; i++)
            {
                var shifted = Math.Min(count - 1, i / ratio);
                var left = (int)shifted;
                var right = Math.Min(count - 1, left + 1);
                var next = power[left] + (power[right] - power[left]) * (shifted - left);
                var gain = (float)Math.Clamp(Math.Sqrt((next + floor) / (power[i] + floor)), 0.25, 4);
                spectrum[i].X *= gain;
                spectrum[i].Y *= gain;
                if (i == 0 || i == size / 2) continue;
                spectrum[size - i].X *= gain;
                spectrum[size - i].Y *= gain;
            }
        }, ct);
    }

    private static void Smooth(double[] source, double[] destination, double[] prefix, int radius)
    {
        for (var i = 0; i < source.Length; i++)
            prefix[i + 1] = prefix[i] + source[i];
        for (var i = 0; i < source.Length; i++)
        {
            var first = Math.Max(0, i - radius);
            var end = Math.Min(source.Length, i + radius + 1);
            destination[i] = (prefix[end] - prefix[first]) / (end - first);
        }
    }

    private static ISampleProvider ShiftPitch(ISampleProvider source, float factor, CancellationToken ct)
    {
        var channels = source.WaveFormat.Channels;
        var input = SpeechSampleBuffer.ReadAll(source, ct).Span;
        var frames = input.Length / channels;
        var result = new float[frames * channels];
        var block = new float[4096];
        for (var channel = 0; channel < channels && frames > 0; channel++)
        {
            var shifter = new SmbPitchShifter();
            // Flush the analysis window and discard its delay, retaining the same number of frames.
            for (var offset = 0; offset < frames + PitchFrame; offset += block.Length)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(block.Length, frames + PitchFrame - offset);
                for (var i = 0; i < count; i++)
                    block[i] = offset + i < frames ? input[(offset + i) * channels + channel] : 0;
                shifter.PitchShift(factor, count, PitchFrame, PitchOverlap, source.WaveFormat.SampleRate, block);
                for (var i = Math.Max(0, PitchFrame - offset); i < count; i++)
                    result[(offset + i - PitchFrame) * channels + channel] = block[i];
            }
        }
        return Limit(source.WaveFormat, result, ct);
    }

    private static ISampleProvider Limit(ISampleProvider source, CancellationToken ct) =>
        Limit(source.WaveFormat, SpeechSampleBuffer.ReadAll(source, ct).ToArray(), ct);

    private static ISampleProvider Limit(WaveFormat format, float[] samples, CancellationToken ct)
    {
        var peak = 1f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        ct.ThrowIfCancellationRequested();
        if (peak > 1)
            for (var i = 0; i < samples.Length; i++) samples[i] /= peak;
        return new SpeechSampleBuffer(format, samples);
    }
}
