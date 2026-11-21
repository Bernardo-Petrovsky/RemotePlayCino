using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RemotePlay;

internal static class VideoFeed
{
    private const byte Jpeg = 0;
    private const byte H264 = 1;
    internal const byte SharedH264 = 2;
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private enum DecodedFormat { Bgra, Bgr24, Nv12 }
    private sealed record EncoderPlan(string[] Arguments, string Codec);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    // A frame that is late by seconds is usually a network or driver hiccup; give it time before giving up.
    private static readonly TimeSpan FrameStallLimit = TimeSpan.FromSeconds(20);
    private static long receivedBytes;
    private static long bytesAtLastFrame;
    private static long lastFrameTick = Environment.TickCount64;
    private static readonly string FFmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
    internal static bool HasFFmpeg => File.Exists(FFmpegPath);

    // Primary monitor first, then the rest in Windows order. Index 0 matches the first capture output.
    internal static Screen[] ScreenList() =>
        [.. Screen.AllScreens.OrderByDescending(screen => screen.Primary)];

    internal static async Task SendAsync(Stream stream, bool modern, bool wantsH264,
        Action<string> status, CancellationToken stop, VideoQuality? requested = null, int encoder = 0,
        int display = 0, bool lowLatency = false, SharedVideoHub? shared = null)
    {
        VideoQuality quality = requested ?? Qualities.Limit(Qualities.Decode(Qualities.DefaultCode), 0, 0);
        int fps = quality.Fps;
        int bufferKilobits = Math.Max(50, quality.Mbps * 1000 / fps);
        Screen[] screens = ScreenList();
        if (wantsH264 && HasFFmpeg && screens.Length > 0)
        {
            int outputIndex = display >= 0 && display < screens.Length ? display : 0;
            Rectangle bounds = screens[outputIndex].Bounds;
            Size size = OutputSize(bounds.Size, quality);
            string dimensions = $"{size.Width}:{size.Height}";
            string? graphicsCapture = GraphicsCapture(bounds, size, fps);
            string[] hardware = [
                "-fps_mode", "passthrough", "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll",
                "-profile:v", "high", "-spatial-aq", "1", "-zerolatency", "1", "-delay", "0",
                "-bf", "0", "-g", $"{fps}", "-rc", "cbr", "-b:v", $"{quality.Mbps}M",
                "-maxrate", $"{quality.Mbps}M", "-bufsize", $"{bufferKilobits * 3}k"
            ];
            string[] software = [
                "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "superfast", "-tune", "zerolatency",
                "-threads:v", fps > 60 ? "6" : "8", "-bf", "0", "-g", $"{fps}", "-crf", "20",
                .. (fps > 60 ? new[] { "-x264-params", "sliced-threads=0" } : []),
                "-maxrate", $"{quality.Mbps}M", "-bufsize", $"{bufferKilobits * 3}k"
            ];
            string[] desktopDuplication = [
                "-filter_complex", $"ddagrab=output_idx={outputIndex}:framerate={fps},setpts=N/({fps}*TB)",
                ..hardware
            ];
            string[] gdi = [
                "-f", "gdigrab", "-framerate", $"{fps}", "-offset_x", $"{bounds.Left}",
                "-offset_y", $"{bounds.Top}", "-video_size", $"{bounds.Width}x{bounds.Height}", "-i", "desktop",
                "-vf", $"scale={dimensions}:flags=bicubic:out_color_matrix=bt709,format=yuv420p",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                "-threads:v", "4", "-bf", "0", "-g", $"{fps}", "-crf", "20",
                "-maxrate", $"{quality.Mbps}M", "-bufsize", $"{bufferKilobits * 3}k"
            ];
            var plans = new List<EncoderPlan>(4);
            // encoder: 0 automatic, 1 NVIDIA only, 2 CPU only.
            if (encoder != 2)
            {
                if (graphicsCapture is not null)
                    plans.Add(new EncoderPlan(["-filter_complex", graphicsCapture, ..hardware], "H.264 · NVIDIA NVENC"));
                plans.Add(new EncoderPlan(desktopDuplication, "H.264 · NVIDIA NVENC"));
            }
            if (encoder != 1)
            {
                if (graphicsCapture is not null)
                    plans.Add(new EncoderPlan(["-filter_complex_threads", "2", "-filter_complex",
                        $"{graphicsCapture},hwdownload,format=bgra,scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p",
                        ..software], "H.264 · CPU"));
                plans.Add(new EncoderPlan(gdi, "H.264 · CPU"));
            }

            if (shared is not null && await shared.TrySendAsync(new(size, fps, quality.Mbps, outputIndex, encoder),
                stream, status, (feed, token) => CaptureSharedAsync(plans, size, feed, token), stop))
                return;

            string[] container = lowLatency
                ? ["-write_index", "0", "-f", "nut"]
                : ["-muxdelay", "0", "-muxpreload", "0", "-omit_video_pes_length", "0", "-f", "mpegts"];
            foreach (EncoderPlan plan in plans)
                if (await RunEncoderAsync(size, plan, container,
                    (source, prefix) => CopyVideoAsync(source, prefix, stream, size, plan.Codec, status, stop), stop))
                    return;
        }

        if (modern)
            await stream.WriteAsync(new byte[] { Jpeg }, stop);
        status("JPEG");
        await SendJpegAsync(stream, modern ? 30 : 15, stop);
    }

    internal static async Task ReceiveAsync(Stream stream, bool modern, Action<string> status,
        Action<VideoFrame> show, CancellationToken stop, byte? preRead = null, VideoDecoder decoder = VideoDecoder.Software,
        bool highFrameRate = false, bool lowLatency = false, bool shared = false)
    {
        if (modern)
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            headerTimeout.CancelAfter(Timeout);
            var kind = new byte[1];
            if (preRead is { } known)
                kind[0] = known;
            else
                await stream.ReadExactlyAsync(kind, headerTimeout.Token);
            switch (kind[0])
            {
                case H264:
                case SharedH264 when shared:
                    var dimensions = new byte[4];
                    await stream.ReadExactlyAsync(dimensions, headerTimeout.Token);
                    var size = new Size(BinaryPrimitives.ReadUInt16LittleEndian(dimensions),
                        BinaryPrimitives.ReadUInt16LittleEndian(dimensions.AsSpan(2)));
                    if (size.Width is < 4 or > 2560 || size.Width % 4 != 0 ||
                        size.Height is < 2 or > 1440 || size.Height % 2 != 0)
                        throw new IOException("Resolución H.264 inválida.");
                    status("H.264");
                    string container = lowLatency ? "nut" : "mpegts";
                    if (kind[0] == SharedH264)
                        container = "flv";
                    await ReceiveH264Async(stream, size, show, stop, decoder, highFrameRate, container);
                    return;
                case Jpeg:
                    break;
                default:
                    throw new IOException("Formato de video desconocido.");
            }
        }
        status("JPEG");
        await ReceiveJpegAsync(stream, show, stop);
    }

    private static Size OutputSize(Size input, VideoQuality quality)
    {
        double factor = Math.Min(1, Math.Min((double)quality.Width / input.Width, (double)quality.Height / input.Height));
        // Width stays a multiple of four so rows need no padding.
        return new Size(Math.Max(4, (int)(input.Width * factor) / 4 * 4),
            Math.Max(2, (int)(input.Height * factor) / 2 * 2));
    }

    private static string? GraphicsCapture(Rectangle bounds, Size size, int fps)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        IntPtr monitor = MonitorFromPoint(center, 0);
        return monitor == IntPtr.Zero ? null :
            $"gfxcapture=hmonitor={unchecked((ulong)monitor.ToInt64())}:max_framerate={fps}:" +
            $"width={size.Width}:height={size.Height}:resize_mode=scale_aspect:scale_mode=bicubic,fps={fps}";
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    private static async Task CaptureSharedAsync(List<EncoderPlan> plans, Size size, SharedVideoHub.Feed feed,
        CancellationToken stop)
    {
        foreach (EncoderPlan plan in plans)
            if (await RunEncoderAsync(size, plan, ["-flvflags", "no_duration_filesize+no_metadata", "-f", "flv"],
                (source, prefix) => feed.ReadAsync(source, prefix, plan.Codec, stop), stop))
                return;
    }

    private static async Task<bool> RunEncoderAsync(Size size, EncoderPlan plan, string[] container,
        Func<Stream, ReadOnlyMemory<byte>, Task> forward, CancellationToken stop)
    {
        Process encoder;
        try
        {
            encoder = StartFFmpeg(false, [.. plan.Arguments, "-an", "-flush_packets", "1", .. container, "pipe:1"]);
        }
        catch (Win32Exception ex)
        {
            Trace.Note($"{plan.Codec}: Windows no pudo iniciar o registrar FFmpeg. {ex.Message}");
            return false;
        }

        using (encoder)
        using (stop.Register(() => Stop(encoder)))
        {
            Task<string> errors = encoder.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                Stream encoded = encoder.StandardOutput.BaseStream;
                var transfer = new byte[16 * 1024];
                int read;
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(3));
                    using var timeout = startup.Token.Register(() => Stop(encoder));
                    try { read = await encoded.ReadAsync(transfer, startup.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { read = 0; }
                    stop.ThrowIfCancellationRequested();
                    if (startup.IsCancellationRequested)
                        read = 0;
                }
                if (read == 0)
                {
                    Stop(encoder);
                    await ((Task)errors.WaitAsync(TimeSpan.FromSeconds(2), stop))
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    string detail = errors.IsCompletedSuccessfully ? errors.Result.Trim() : "";
                    Trace.Note($"{plan.Codec}: no se recibieron datos al iniciar ({size.Width}x{size.Height}). {detail}");
                    return false;
                }

                Trace.Note($"{plan.Codec}: primer dato del codificador ({read} bytes).");
                await forward(encoded, transfer.AsMemory(0, read));
                throw Ended("La captura H.264 se detuvo", await errors);
            }
            finally
            {
                Stop(encoder);
                await ((Task)errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task CopyVideoAsync(Stream source, ReadOnlyMemory<byte> prefix, Stream target, Size size,
        string codec, Action<string> status, CancellationToken stop)
    {
        var header = new byte[5];
        header[0] = H264;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)size.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), (ushort)size.Height);
        await target.WriteAsync(header, stop);
        Trace.Note($"{codec}: encabezado enviado al cliente.");
        status(codec);
        var buffer = new byte[16 * 1024];
        ReadOnlyMemory<byte> part = prefix;
        while (!part.IsEmpty)
        {
            Trace.HostChunk(part.Length);
            long writeStart = Trace.Now;
            await target.WriteAsync(part, stop);
            if (Trace.Enabled) Trace.Log("host_quic_write_ms", Trace.Ms(writeStart, Trace.Now));
            int read = await source.ReadAsync(buffer, stop);
            part = buffer.AsMemory(0, read);
        }
    }

    private static async Task ReceiveH264Async(Stream stream, Size size, Action<VideoFrame> show, CancellationToken stop,
        VideoDecoder decoderType, bool highFrameRate, string container)
    {
        bool hardwareDecode = decoderType != VideoDecoder.Software;
        bool cuda = decoderType == VideoDecoder.Nvidia;
        bool highResolution = size.Width > 1920 || size.Height > 1080;
        DecodedFormat format = highResolution ? DecodedFormat.Bgr24 : DecodedFormat.Bgra;
        string scale = $"scale={size.Width}:{size.Height}:flags=bicubic";
        if ((cuda || (highResolution && hardwareDecode && highFrameRate)) && Sse2.IsSupported && size.Width % 16 == 0)
        {
            // Deliver compact decoder output over IPC and expand to BGRA directly in the viewer's frame buffer.
            format = DecodedFormat.Nv12;
            scale += ":out_color_matrix=bt601:out_range=tv";
        }
        if (cuda)
            scale = "hwdownload,format=nv12," + scale;
        bool usePipe = highResolution || cuda || (hardwareDecode && highFrameRate && size.Height >= 1080);
        string? pipeName = usePipe ? $"RemotePlay-Video-{Guid.NewGuid():N}" : null;
        using var videoPipe = pipeName is not null ? OpenDecodedVideoPipe(pipeName) : null;
        string[] output = pipeName is not null
            ? ["-avioflags", "direct", "-y", "-seekable", "0", $@"file:\\.\pipe\{pipeName}"]
            : ["pipe:1"];
        string filterThreads = highResolution ? "4" : "2";
        if (!highFrameRate)
            filterThreads = "1";
        string[] decoding = highFrameRate && !hardwareDecode
            ? ["-threads", "4", "-thread_type", "frame+slice"]
            : ["-threads", "1", "-flags", "low_delay"];
        string[] acceleration = decoderType switch
        {
            VideoDecoder.Nvidia => ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"],
            VideoDecoder.D3d11 => ["-hwaccel", "d3d11va"],
            _ => []
        };
        using Process decoder = StartFFmpeg(true, [
            "-protocol_whitelist", "pipe", "-probesize", "8192", "-analyzeduration", "1",
            ..decoding, "-fflags", "discardcorrupt", "-max_delay", "0", "-thread_queue_size", "1",
            .. acceleration,
            "-f", container, "-i", "pipe:0",
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", scale,
            "-pix_fmt", format switch { DecodedFormat.Nv12 => "nv12", DecodedFormat.Bgr24 => "bgr24", _ => "bgra" },
            "-filter_threads", filterThreads,
            "-c:v", "rawvideo", "-threads:v", "1", "-fps_mode", "passthrough",
            "-flush_packets", "1", "-f", "rawvideo", .. output
        ]);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        using var stopOnTimeout = deadline.Token.Register(() => Stop(decoder));
        Task<string> errors = decoder.StandardError.ReadToEndAsync(CancellationToken.None);
        Task input = CopyToDecoderAsync(stream, decoder.StandardInput.BaseStream, linked.Token);
        Task frames = videoPipe is null
            ? ReadBitmapsAsync(decoder.StandardOutput.BaseStream, size, show, deadline, format)
            : ReadPipeFramesAsync(videoPipe, decoder.Id, size, show, deadline, format);
        Volatile.Write(ref lastFrameTick, Environment.TickCount64);
        Volatile.Write(ref bytesAtLastFrame, Volatile.Read(ref receivedBytes));
        Task monitor = MonitorStallsAsync(linked.Token);
        try
        {
            if (await Task.WhenAny(input, frames) == frames)
                await frames;
            else
                await input;
        }
        catch (EndOfStreamException) when (!stop.IsCancellationRequested && decoder.HasExited)
        {
            throw Ended("El decodificador H.264 se detuvo", await errors);
        }
        finally
        {
            await linked.CancelAsync();
            await Task.WhenAll(input, frames, errors, monitor).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (errors.IsCompletedSuccessfully)
                Trace.Note($"Decodificador terminó ({size.Width}x{size.Height}). " + errors.Result.Trim());
        }
    }

    // Tells a network stall (no video bytes arriving) from a decoder stall (bytes arrive, no frames come out).
    private static async Task MonitorStallsAsync(CancellationToken stop)
    {
        long reportedFor = 0, nextReport = 0, worst = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stop))
            {
                long now = Environment.TickCount64;
                long last = Volatile.Read(ref lastFrameTick);
                long silent = now - last;
                if (silent < 1500)
                {
                    if (reportedFor != 0)
                    {
                        ConnectionLog.Note($"Cliente · video recuperado tras una parada de unos {worst} ms.");
                        reportedFor = 0;
                        worst = 0;
                    }
                    continue;
                }
                worst = Math.Max(worst, silent);
                if (reportedFor == last && now < nextReport)
                    continue;
                reportedFor = last;
                nextReport = now + 3000;
                long arrived = Volatile.Read(ref receivedBytes) - Volatile.Read(ref bytesAtLastFrame);
                ConnectionLog.Note($"Cliente · sin cuadros desde hace {silent} ms; desde el último cuadro llegaron {arrived} bytes de video " +
                    (arrived > 65536 ? "(llegan datos: el atasco está en el decodificador)." : "(casi nada: el atasco está en la red o el anfitrión)."));
            }
        }
        catch (OperationCanceledException) { /* The session ended. */ }
    }

    private static NamedPipeServerStream OpenDecodedVideoPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("No se pudo identificar al usuario de Windows.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4 * 1024 * 1024, 0, security);
    }

    private static async Task ReadPipeFramesAsync(NamedPipeServerStream pipe, int decoderId, Size size,
        Action<VideoFrame> show, CancellationTokenSource deadline, DecodedFormat format)
    {
        deadline.CancelAfter(Timeout);
        await pipe.WaitForConnectionAsync(deadline.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId != decoderId)
            throw new IOException("La tubería de video recibió una conexión ajena al decodificador.");
        await ReadBitmapsAsync(pipe, size, show, deadline, format);
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    private static async Task CopyToDecoderAsync(Stream source, Stream decoderInput, CancellationToken stop)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, stop)) != 0)
        {
            Interlocked.Add(ref receivedBytes, read);
            long writeStart = Trace.Now;
            await decoderInput.WriteAsync(buffer.AsMemory(0, read), stop);
            if (Trace.Enabled)
            {
                Trace.Log("client_recv_chunk_bytes", read);
                Trace.Log("client_decoder_stdin_write_ms", Trace.Ms(writeStart, Trace.Now));
            }
        }
    }

    private static async Task ReadBitmapsAsync(Stream output, Size size, Action<VideoFrame> show,
        CancellationTokenSource deadline, DecodedFormat format)
    {
        bool packedRgb = format == DecodedFormat.Bgr24;
        int bytesPerPixel = packedRgb ? 3 : 4;
        int bytesPerFrame = size.Width * size.Height * bytesPerPixel;
        var pool = ArrayPool<byte>.Create(bytesPerFrame, 3);
        byte[]? nv12 = format == DecodedFormat.Nv12
            ? GC.AllocateUninitializedArray<byte>(size.Width * size.Height * 3 / 2)
            : null;
        while (!deadline.IsCancellationRequested)
        {
            deadline.CancelAfter(FrameStallLimit);
            byte[] pixels = pool.Rent(bytesPerFrame);
            VideoFrame frame;
            long buildStart;
            try
            {
                if (nv12 is null)
                    await output.ReadExactlyAsync(pixels.AsMemory(0, bytesPerFrame), deadline.Token);
                else
                {
                    await output.ReadExactlyAsync(nv12, deadline.Token);
                    Nv12Conversion.CopyToBgra(nv12, pixels, size.Width, size.Height);
                }
                Trace.FrameReady();
                Volatile.Write(ref lastFrameTick, Environment.TickCount64);
                Volatile.Write(ref bytesAtLastFrame, Volatile.Read(ref receivedBytes));
                buildStart = Trace.Now;
                frame = new VideoFrame(pool, pixels, size, packedRgb);
            }
            catch
            {
                pool.Return(pixels);
                throw;
            }
            try
            {
                if (Trace.Enabled) Trace.Log("client_bitmap_build_ms", Trace.Ms(buildStart, Trace.Now));
                show(frame);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
        deadline.Token.ThrowIfCancellationRequested();
    }

    private static Process StartFFmpeg(bool redirectInput, string[] arguments)
    {
        var command = new ProcessStartInfo(FFmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error" }.Concat(arguments))
            command.ArgumentList.Add(argument);
        Trace.Note("ffmpeg " + string.Join(' ', command.ArgumentList));
        ChildJob.EnsureAvailable();
        Process process = Process.Start(command) ?? throw new IOException("No se pudo iniciar FFmpeg.");
        try
        {
            ChildJob.Add(process);
            return process;
        }
        catch
        {
            using (process)
                Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { /* The process already exited. */ }
        catch (Win32Exception) when (process.HasExited) { /* It exited during Kill. */ }
    }

    private static IOException Ended(string title, string errors)
    {
        Trace.Note($"{title}: {errors.Trim()}");
        string detail = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        return new IOException(detail.Length > 0 ? $"{title}. {detail}" : $"{title}.");
    }

    private static async Task SendJpegAsync(Stream stream, int fps, CancellationToken stop)
    {
        using var capture = new ScreenCapture();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        var length = new byte[4];
        do
        {
            ReadOnlyMemory<byte> frame = capture.Grab();
            BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
            await stream.WriteAsync(length, stop);
            await stream.WriteAsync(frame, stop);
        } while (await timer.WaitForNextTickAsync(stop));
    }

    private static async Task ReceiveJpegAsync(Stream stream, Action<VideoFrame> show, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var header = new byte[4];
        var buffer = new byte[MaxFrameBytes];
        while (true)
        {
            deadline.CancelAfter(FrameStallLimit);
            await stream.ReadExactlyAsync(header, deadline.Token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaxFrameBytes)
                throw new IOException("Tamaño de imagen inválido.");
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), deadline.Token);
            using var encoded = new MemoryStream(buffer, 0, length, writable: false);
            using Image decoded = Image.FromStream(encoded);
            if (decoded.Width > ScreenCapture.MaxWidth || decoded.Height > ScreenCapture.MaxHeight)
                throw new IOException("Resolución de imagen inválida.");
            show(new VideoFrame(new Bitmap(decoded)));
        }
    }
}
