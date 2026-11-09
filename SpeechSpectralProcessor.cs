using NAudio.Dsp;
using NAudio.Wave;

namespace TwitchChatAgent;

internal sealed class SpeechSpectralProcessor
{
    private readonly int order;
    private readonly int size;
    private readonly int hop;
    private readonly float[] window;
    private readonly float[] synthesisWindow;

    public SpeechSpectralProcessor(int order)
    {
        this.order = order;
        size = 1 << order;
        hop = size / 4;
        window = new float[size];
        synthesisWindow = new float[size];
        for (var i = 0; i < size; i++)
        {
            var phase = Math.Tau * i / size;
            window[i] = (float)(0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase));
        }
        for (var phase = 0; phase < hop; phase++)
        {
            var sum = 0f;
            for (var i = phase; i < size; i += hop)
                sum += window[i] * window[i];
            for (var i = phase; i < size; i += hop)
                synthesisWindow[i] = window[i] / sum;
        }
    }

    public ISampleProvider Apply(ISampleProvider source, Action<Complex[]> process, CancellationToken ct)
    {
        var channels = source.WaveFormat.Channels;
        var input = SpeechSampleBuffer.ReadAll(source, ct).Span;
        var frameCount = input.Length / channels;
        var output = new float[frameCount * channels];
        var spectrum = new Complex[size];

        // Zero-pad both ends and overlap the windows to keep the original duration.
        for (var start = hop - size; start < frameCount; start += hop)
        {
            ct.ThrowIfCancellationRequested();
            var first = Math.Max(0, -start);
            var end = Math.Min(size, frameCount - start);
            for (var channel = 0; channel < channels; channel++)
            {
                Array.Clear(spectrum);
                for (var i = first; i < end; i++)
                    spectrum[i].X = input[(start + i) * channels + channel] * window[i];
                FastFourierTransform.FFT(true, order, spectrum);
                process(spectrum);
                FastFourierTransform.FFT(false, order, spectrum);
                for (var i = first; i < end; i++)
                    output[(start + i) * channels + channel] += spectrum[i].X * synthesisWindow[i];
            }
        }

        return new SpeechSampleBuffer(source.WaveFormat, output);
    }
}
