using System.Buffers;
using NAudio.Dsp;
using NAudio.Wave;

namespace TwitchChatAgent;

internal static class SpeechFftFilter
{
    private const int FftOrder = 13;
    private const int FftSize = 1 << FftOrder;
    private const int Hop = FftSize / 4;
    private static readonly Dictionary<SpeechEffect, ResponseCurve> Presets = new()
    {
        [SpeechEffect.VoiceMail] = new(
        [
            (20, -300), (172.8, -132.7), (326.7, -74.7), (482.9802, -50.6261),
            (600, -24), (700, 0), (2000.0001, 0), (2399.9999, -12),
            (3170.2766, -40.185), (4487.0296, -70.3369), (7027.8418, -115.3369),
            (11495.8956, -300), (22049.9997, -300), (24000, -300)
        ], logarithmic: false),
        [SpeechEffect.Reciver] = new(
        [
            (20, -300), (290, -300), (497.1657, -73.1841), (778.6894, -39.5834),
            (1199.9999, -12), (1484.9999, 0), (3200.0002, 0), (4000.0001, -300),
            (22049.9997, -300), (24000, -300)
        ]),
        [SpeechEffect.UnderwaterShallow] = new(
        [
            (20, 0), (400, 0), (1938.1008, -300), (22049.9997, -300), (24000, -300)
        ], spline: true),
        [SpeechEffect.UnderwaterDeeper] = new(
        [
            (20, 0), (150, 0), (152, -300), (654.5, -300), (22049.9997, -300), (24000, -300)
        ], spline: true),
        [SpeechEffect.ClubDownstairs] = new(
        [
            (20, -6), (64, 0), (255, 0), (780, -6), (800, -96), (2000, -300),
            (22049.9997, -300), (24000, -300)
        ], spline: true),
        [SpeechEffect.OnlyTheTwitter] = new(
        [
            (20, -300), (1000, -300), (2000, 0), (22049.9997, 0), (24000, 0)
        ])
    };
    private static readonly float[] Window = CreateWindow();
    private static readonly float[] SynthesisWindow = CreateSynthesisWindow();

    public static bool Supports(SpeechEffect effect) => Presets.ContainsKey(effect);

    public static ISampleProvider Apply(ISampleProvider source, SpeechEffect effect, int intensity, CancellationToken ct)
    {
        var wet = Math.Clamp(intensity, 0, 100) / 100f;
        if (wet == 0)
            return source;

        var channels = source.WaveFormat.Channels;
        var input = new ArrayBufferWriter<float>();
        var buffer = new float[Hop * channels];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            input.Write(buffer.AsSpan(0, read));
        }

        var frameCount = input.WrittenCount / channels;
        var output = new float[frameCount * channels];
        var gains = CreateBinGains(source.WaveFormat.SampleRate, wet, Presets[effect]);
        var spectrum = new Complex[FftSize];
        var dry = input.WrittenSpan;

        // Zero-pad both ends and overlap the windows to keep the original duration.
        for (var start = Hop - FftSize; start < frameCount; start += Hop)
        {
            ct.ThrowIfCancellationRequested();
            var first = Math.Max(0, -start);
            var end = Math.Min(FftSize, frameCount - start);
            for (var channel = 0; channel < channels; channel++)
            {
                Array.Clear(spectrum);
                for (var i = first; i < end; i++)
                    spectrum[i].X = dry[(start + i) * channels + channel] * Window[i];

                FastFourierTransform.FFT(true, FftOrder, spectrum);
                for (var i = 0; i < FftSize; i++)
                {
                    spectrum[i].X *= gains[i];
                    spectrum[i].Y *= gains[i];
                }
                FastFourierTransform.FFT(false, FftOrder, spectrum);

                for (var i = first; i < end; i++)
                    output[(start + i) * channels + channel] += spectrum[i].X * SynthesisWindow[i];
            }
        }

        return new FilteredAudio(source.WaveFormat, output);
    }

    private static float[] CreateBinGains(int sampleRate, float wet, ResponseCurve curve)
    {
        var gains = new float[FftSize];
        for (var bin = 0; bin <= FftSize / 2; bin++)
        {
            var hz = (double)bin * sampleRate / FftSize;
            var db = curve.LevelAt(hz);
            var gain = (1 - wet) + wet * (float)Math.Pow(10, db / 20);
            gains[bin] = gains[(FftSize - bin) % FftSize] = gain;
        }
        return gains;
    }

    private sealed class ResponseCurve
    {
        private readonly (double X, double Db)[] points;
        private readonly bool logarithmic;
        private readonly double[]? tangents;

        public ResponseCurve((double Hz, double Db)[] points, bool logarithmic = true, bool spline = false)
        {
            this.logarithmic = logarithmic;
            this.points = points.Select(point => (logarithmic ? Math.Log(point.Hz) : point.Hz, point.Db)).ToArray();
            if (!spline)
                return;

            // Monotone Hermite interpolation preserves flat bands and avoids overshoot at steep cutoffs.
            tangents = new double[points.Length];
            for (var i = 1; i < points.Length - 1; i++)
            {
                var previousWidth = this.points[i].X - this.points[i - 1].X;
                var nextWidth = this.points[i + 1].X - this.points[i].X;
                var previousSlope = (points[i].Db - points[i - 1].Db) / previousWidth;
                var nextSlope = (points[i + 1].Db - points[i].Db) / nextWidth;
                if (previousSlope * nextSlope > 0)
                {
                    var previousWeight = 2 * nextWidth + previousWidth;
                    var nextWeight = nextWidth + 2 * previousWidth;
                    tangents[i] = (previousWeight + nextWeight) /
                        (previousWeight / previousSlope + nextWeight / nextSlope);
                }
            }
        }

        public double LevelAt(double hz)
        {
            var x = logarithmic ? Math.Log(hz) : hz;
            var segment = 0;
            while (segment < points.Length - 2 && x > points[segment + 1].X)
                segment++;

            var left = points[segment];
            var right = points[segment + 1];
            var width = right.X - left.X;
            var t = Math.Clamp((x - left.X) / width, 0, 1);
            var difference = right.Db - left.Db;
            if (tangents is null)
                return left.Db + difference * t;

            var remaining = 1 - t;
            return left.Db + difference * t * t * (3 - 2 * t) +
                width * t * remaining * (remaining * tangents[segment] - t * tangents[segment + 1]);
        }
    }

    private static float[] CreateWindow()
    {
        var window = new float[FftSize];
        for (var i = 0; i < window.Length; i++)
        {
            var phase = Math.Tau * i / FftSize;
            window[i] = (float)(0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase));
        }
        return window;
    }

    private static float[] CreateSynthesisWindow()
    {
        var window = new float[FftSize];
        for (var phase = 0; phase < Hop; phase++)
        {
            var sum = 0f;
            for (var i = phase; i < FftSize; i += Hop)
                sum += Window[i] * Window[i];
            for (var i = phase; i < FftSize; i += Hop)
                window[i] = Window[i] / sum;
        }
        return window;
    }

    private sealed class FilteredAudio(WaveFormat format, float[] samples) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat => format;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - position);
            // NAudio's playback adapter can expose a byte buffer as float samples.
            for (var i = 0; i < read; i++)
                buffer[offset + i] = samples[position + i];
            position += read;
            return read;
        }
    }
}
