using System.Buffers;
using NAudio.Wave;

namespace RemotePlay;

// Stream reads may end inside a PCM sample. Only complete interleaved frames reach the speaker.
internal sealed class PcmPlaybackBuffer : IWaveProvider
{
    private readonly Lock gate = new();
    private readonly BufferedWaveProvider playback;
    private readonly byte[] partial;
    private readonly int recoveryBytes;
    private int pending;

    internal PcmPlaybackBuffer(WaveFormat format, int capacityMilliseconds = 120, int recoveryMilliseconds = 40)
    {
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels is < 1 or > 2)
            throw new ArgumentException("Se requiere audio PCM de 16 bits mono o estéreo.", nameof(format));
        if (capacityMilliseconds < 1 || recoveryMilliseconds < 1 || recoveryMilliseconds > capacityMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds));
        int capacity = BytesFor(format, capacityMilliseconds);
        recoveryBytes = Math.Min(capacity, BytesFor(format, recoveryMilliseconds));
        partial = new byte[format.BlockAlign];
        playback = new BufferedWaveProvider(format)
        { BufferLength = capacity, DiscardOnBufferOverflow = false, ReadFully = false };
    }

    public WaveFormat WaveFormat => playback.WaveFormat;
    internal int BufferedBytes { get { lock (gate) return playback.BufferedBytes; } }

    private static int BytesFor(WaveFormat format, int milliseconds) =>
        checked(Math.Max(1, (int)((long)format.SampleRate * milliseconds / 1000)) * format.BlockAlign);

    internal void AddSamples(byte[] source, int offset, int count)
    {
        ReadOnlySpan<byte> input = source.AsSpan(offset, count);
        lock (gate)
        {
            int complete = checked(pending + count) / partial.Length * partial.Length;
            if (complete == 0)
            {
                input.CopyTo(partial.AsSpan(pending));
                pending += count;
                return;
            }
            byte[] joined = ArrayPool<byte>.Shared.Rent(complete);
            try
            {
                partial.AsSpan(0, pending).CopyTo(joined);
                int consumed = complete - pending;
                input[..consumed].CopyTo(joined.AsSpan(pending));
                pending = input.Length - consumed;
                input[consumed..].CopyTo(partial);
                int start = 0;
                int length = complete;
                if (playback.BufferedBytes + (long)complete > playback.BufferLength)
                {
                    // A delayed burst must not replay a long backlog or discard half a stereo frame.
                    playback.ClearBuffer();
                    length = Math.Min(complete, recoveryBytes);
                    start = complete - length;
                }
                playback.AddSamples(joined, start, length);
            }
            finally { ArrayPool<byte>.Shared.Return(joined, clearArray: true); }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        _ = buffer.AsSpan(offset, count); // Validate before consuming queued data.
        if (count % partial.Length != 0)
            throw new ArgumentException("La reproducción debe pedir fotogramas de audio completos.", nameof(count));
        lock (gate)
        {
            int received = playback.Read(buffer, offset, count);
            buffer.AsSpan(offset + received, count - received).Clear();
            return count;
        }
    }
}
