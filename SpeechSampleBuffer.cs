using System.Buffers;
using NAudio.Wave;

namespace TwitchChatAgent;

internal sealed class SpeechSampleBuffer(WaveFormat format, float[] samples) : ISampleProvider
{
    private int position;
    public WaveFormat WaveFormat => format;

    public static ReadOnlyMemory<float> ReadAll(ISampleProvider source, CancellationToken ct)
    {
        var input = new ArrayBufferWriter<float>();
        var buffer = new float[2048 * source.WaveFormat.Channels];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return input.WrittenMemory;
            input.Write(buffer.AsSpan(0, read));
        }
    }

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
