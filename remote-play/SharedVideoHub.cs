using System.Buffers.Binary;
using System.Threading.Channels;

namespace RemotePlay;

internal sealed class SharedVideoHub
{
    // Over the internet a write can stall for 2-3 s without the guest being gone.
    private static readonly TimeSpan WriteStallLimit = TimeSpan.FromSeconds(25);

    private readonly Lock gate = new();
    private readonly Dictionary<Profile, Feed> feeds = [];

    internal readonly record struct Profile(Size Size, int Fps, int Mbps, int Display, int Encoder);

    internal async Task<bool> TrySendAsync(Profile profile, Stream video, Action<string> status,
        Func<Feed, CancellationToken, Task> capture, CancellationToken stop)
    {
        var subscriber = new Subscriber();
        Feed feed;
        lock (gate)
        {
            if (!feeds.TryGetValue(profile, out feed!))
            {
                feed = new Feed(gate);
                feeds.Add(profile, feed);
                feed.Subscribers.Add(subscriber);
                feed.Worker = Task.Run(() => CaptureAsync(profile, feed, capture), CancellationToken.None);
            }
            else
                feed.Subscribers.Add(subscriber);
        }

        try
        {
            if (!await subscriber.Packets.Reader.WaitToReadAsync(stop))
                return false;

            var header = new byte[5];
            header[0] = VideoFeed.SharedH264;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)profile.Size.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), (ushort)profile.Size.Height);
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(stop);
            writing.CancelAfter(WriteStallLimit);
            await video.WriteAsync(header, writing.Token);
            status(feed.Codec);
            await foreach (byte[] packet in subscriber.Packets.Reader.ReadAllAsync(stop))
            {
                writing.CancelAfter(WriteStallLimit);
                await video.WriteAsync(packet, writing.Token);
            }
            throw new EndOfStreamException("La captura de video compartida se detuvo.");
        }
        catch (OperationCanceledException ex) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException($"El envío de video al invitado no pudo avanzar durante {WriteStallLimit.TotalSeconds:0} segundos.", ex);
        }
        finally
        {
            bool last;
            lock (gate)
            {
                feed.Subscribers.Remove(subscriber);
                last = feed.Subscribers.Count == 0;
                if (last)
                    Forget(profile, feed);
            }
            if (last)
            {
                try { await feed.Stop.CancelAsync(); }
                finally
                {
                    await feed.Worker;
                    feed.Stop.Dispose();
                }
            }
        }
    }

    private async Task CaptureAsync(Profile profile, Feed feed, Func<Feed, CancellationToken, Task> capture)
    {
        try { await capture(feed, feed.Stop.Token); }
        catch (OperationCanceledException) when (feed.Stop.IsCancellationRequested) { /* Last guest left. */ }
        catch (Exception ex)
        {
            ConnectionLog.Write("Anfitrión", "Captura de video compartida", ex);
            Trace.Note($"Captura compartida terminada. {ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                Forget(profile, feed);
                foreach (Subscriber subscriber in feed.Subscribers)
                    subscriber.Packets.Writer.TryComplete();
            }
        }
    }

    private void Forget(Profile profile, Feed feed)
    {
        if (feeds.TryGetValue(profile, out Feed? current) && ReferenceEquals(current, feed))
            feeds.Remove(profile);
    }

    internal sealed class Subscriber
    {
        // A decoder may take several hundred milliseconds to start. A full queue resumes at a keyframe.
        internal Channel<byte[]> Packets { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        internal bool Started;
        internal bool WaitingForKeyframe = true;
    }

    internal sealed class Feed(Lock gate)
    {
        private const int TagHeaderSize = 11;
        private const int MaxTagBytes = 4 * 1024 * 1024;
        internal List<Subscriber> Subscribers { get; } = [];
        internal CancellationTokenSource Stop { get; } = new();
        internal Task Worker { get; set; } = Task.CompletedTask;
        internal string Codec { get; private set; } = "H.264";

        // FFmpeg writes an FLV header, the AVC configuration and complete video tags.
        // Each new viewer starts at a live keyframe with the latest decoder configuration.
        internal async Task ReadAsync(Stream source, ReadOnlyMemory<byte> prefix, string codec, CancellationToken stop)
        {
            var header = new byte[13];
            await ReadExactlyAsync(header);
            if (!header.AsSpan(0, 4).SequenceEqual("FLV\u0001"u8) || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) != 9)
                throw new IOException("Encabezado FLV inválido del codificador.");
            Codec = codec + " · compartido";
            byte[]? config = null;
            var tagHeader = new byte[TagHeaderSize];
            while (!stop.IsCancellationRequested)
            {
                await ReadExactlyAsync(tagHeader);
                int size = tagHeader[1] << 16 | tagHeader[2] << 8 | tagHeader[3];
                if (size > MaxTagBytes)
                    throw new IOException("El codificador produjo un paquete de video demasiado grande.");
                var tag = new byte[TagHeaderSize + size + sizeof(uint)];
                tagHeader.CopyTo(tag, 0);
                await ReadExactlyAsync(tag.AsMemory(TagHeaderSize));
                if (tag[0] != 9 || size < 5 || (tag[TagHeaderSize] & 0x0f) != 7)
                    continue;
                if (tag[TagHeaderSize + 1] == 0)
                {
                    config = tag;
                    Publish(header, config, tag, false, configuration: true);
                }
                else if (config is not null && tag[TagHeaderSize + 1] == 1)
                    Publish(header, config, tag, (tag[TagHeaderSize] >> 4) == 1, configuration: false);
            }

            async ValueTask ReadExactlyAsync(Memory<byte> destination)
            {
                int buffered = Math.Min(destination.Length, prefix.Length);
                prefix[..buffered].CopyTo(destination);
                prefix = prefix[buffered..];
                await source.ReadExactlyAsync(destination[buffered..], stop);
            }
        }

        private void Publish(byte[] header, byte[] config, byte[] tag, bool keyframe, bool configuration)
        {
            lock (gate)
            {
                byte[]? initial = null;
                byte[]? resume = null;
                foreach (Subscriber subscriber in Subscribers)
                {
                    if (subscriber.WaitingForKeyframe)
                    {
                        if (!keyframe || configuration)
                            continue;
                        byte[] start = subscriber.Started ? resume ??= [.. config, .. tag] : initial ??= [.. header, .. config, .. tag];
                        if (subscriber.Packets.Writer.TryWrite(start))
                        {
                            subscriber.Started = true;
                            subscriber.WaitingForKeyframe = false;
                        }
                    }
                    else if (!subscriber.Packets.Writer.TryWrite(tag))
                        subscriber.WaitingForKeyframe = true;
                }
            }
        }
    }
}
