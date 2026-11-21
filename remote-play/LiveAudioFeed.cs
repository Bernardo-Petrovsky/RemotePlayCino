using System.Buffers.Binary;
using System.Threading.Channels;

namespace RemotePlay;

internal static class LiveAudioFeed
{
    internal static async Task SendAsync(Stream stream, LiveHostSettings settings, Action<string> status, CancellationToken stop)
    {
        // One audio stream and one PCM format survive changes of source, filter and mute state.
        var header = new byte[] { AudioFeed.Tag, 1, 0, 0, 0, 0, 2 };
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 48000);
        await stream.WriteAsync(header, stop);
        while (!stop.IsCancellationRequested)
        {
            LiveHostSettings.State state = settings.Current;
            HostOptions options = state.Options;
            if (!options.Audio)
            {
                status("Audio pausado por el anfitrión");
                await state.AudioChanged.WaitAsync(stop);
                continue;
            }
            using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
            using var sink = new PcmSink(packets.Writer);
            Task capture = Task.Run(async () =>
            {
                try
                {
                    await AudioFeed.SendAsync(sink, true, status, captureStop.Token,
                        options.AudioDevice, options.AudioMode, options.AudioApplication, standardFormat: true);
                }
                finally { packets.Writer.TryComplete(); }
            }, CancellationToken.None);
            try
            {
                while (!state.AudioChanged.IsCompleted)
                {
                    Task<bool> ready = packets.Reader.WaitToReadAsync(captureStop.Token).AsTask();
                    if (await Task.WhenAny(ready, state.AudioChanged) == state.AudioChanged) break;
                    if (!await ready)
                    {
                        await capture;
                        status("Audio detenido. Actualiza la aplicación de audio y aplica los cambios.");
                        await state.AudioChanged.WaitAsync(stop);
                        break;
                    }
                    while (!state.AudioChanged.IsCompleted && packets.Reader.TryRead(out byte[]? pcm))
                        // A settings change cancels capture, never an in-progress QUIC write on this persistent stream.
                        await stream.WriteAsync(pcm, stop);
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested && ex is IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or TimeoutException or
                ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                ConnectionLog.Write("Anfitrión", "Audio durante la sesión", ex);
                status($"Audio detenido: {ex.Message}");
                await state.AudioChanged.WaitAsync(stop);
            }
            finally
            {
                await captureStop.CancelAsync();
                await capture.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private sealed class PcmSink(ChannelWriter<byte[]> packets) : Stream
    {
        private readonly byte[] header = new byte[7];
        private int received;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> pcm = buffer.Span;
            if (received < header.Length)
            {
                int take = Math.Min(pcm.Length, header.Length - received);
                pcm[..take].CopyTo(header.AsSpan(received));
                received += take;
                pcm = pcm[take..];
                if (received == header.Length && (header[0] != AudioFeed.Tag || header[1] != 1 || header[6] != 2 ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2)) != 48000))
                    throw new IOException("El audio de la sesión no tiene el formato esperado.");
            }
            if (!pcm.IsEmpty) packets.TryWrite(pcm.ToArray());
            return ValueTask.CompletedTask;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    }
}
