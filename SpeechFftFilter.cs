using NAudio.Wave;

namespace TwitchChatAgent;

internal static class SpeechFftFilter
{
    private const int FftOrder = 13;
    private const int FftSize = 1 << FftOrder;
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
    private static readonly SpeechSpectralProcessor Processor = new(FftOrder);

    public static bool Supports(SpeechEffect effect) => Presets.ContainsKey(effect);

    public static ISampleProvider Apply(ISampleProvider source, SpeechEffect effect, int intensity, CancellationToken ct)
    {
        var wet = Math.Clamp(intensity, 0, 100) / 100f;
        if (wet == 0)
            return source;

        var gains = CreateBinGains(source.WaveFormat.SampleRate, wet, Presets[effect]);
        return Processor.Apply(source, spectrum =>
        {
            for (var i = 0; i < FftSize; i++)
            {
                spectrum[i].X *= gains[i];
                spectrum[i].Y *= gains[i];
            }
        }, ct, compensateLevel: effect == SpeechEffect.VoiceMail);
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
}
