using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RemotePlay;

internal readonly record struct HostOptions(bool Audio, bool Input, int BandwidthMbps = 0, int MaxFps = 0,
    int Encoder = 0, int Display = 0, string? AudioDevice = null, int IdleMinutes = 0, bool LockOnDisconnect = false,
    bool LimitLocal1440 = true, int AudioMode = 0, AudioApplication? AudioApplication = null,
    bool RequireApproval = true, LiveHostSettings? Live = null, HostMousePriority? MousePriority = null);

// Tracks the last time the guest did something, for the idle kick.
internal sealed class IdleTracker(int minutes, Func<int>? currentLimit = null)
{
    private long last = Environment.TickCount64;
    internal void Touch() => Volatile.Write(ref last, Environment.TickCount64);
    internal bool Expired
    {
        get
        {
            int limit = currentLimit?.Invoke() ?? minutes;
            return limit > 0 && Environment.TickCount64 - Volatile.Read(ref last) > limit * 60_000L;
        }
    }
}

internal static class HostActions
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool LockWorkStation();

    internal static void Lock()
    {
        if (!LockWorkStation())
            Trace.Note("Windows no pudo bloquear la sesión al desconectarse el invitado.");
    }
}
internal enum VideoDecoder { Software, D3d11, Nvidia }

internal readonly record struct ClientOptions(bool Audio, Channel<byte[]>? Input, int Quality = Qualities.DefaultCode,
    VideoDecoder Decoder = VideoDecoder.Software, Action<double, bool, GuestDevices?>? NetworkStatus = null, Action<bool>? InputState = null,
    string? GuestName = null, bool SendDesktopInput = true);

internal readonly record struct VideoQuality(string Name, int Width, int Height, int Fps, int Mbps);

internal static class Qualities
{
    internal static readonly (string Name, int Width, int Height)[] Resolutions =
        [("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p (experimental)", 2560, 1440)];
    internal static readonly int[] FpsOptions = [30, 60, 90, 120];
    internal static readonly int[] BandwidthOptions = [10, 20, 30, 50, 100];
    internal const int DefaultCode = 1; // 720p, 60 FPS

    // The client sends a single byte: resolution index * 16 + FPS index.
    internal static int Encode(int resolution, int fps) => resolution * 16 + fps;

    internal static VideoQuality Decode(int code)
    {
        int resolution = code / 16, fps = code % 16;
        if (resolution >= Resolutions.Length || fps >= FpsOptions.Length)
            (resolution, fps) = (0, 1);
        return Make(Resolutions[resolution].Name, Resolutions[resolution].Width, Resolutions[resolution].Height,
            FpsOptions[fps], 0);
    }

    // Applies the host limits. 0 means no limit.
    internal static VideoQuality Limit(VideoQuality quality, int bandwidthMbps, int maxFps)
    {
        int fps = maxFps > 0 ? Math.Min(quality.Fps, maxFps) : quality.Fps;
        return Make(quality.Name, quality.Width, quality.Height, fps, bandwidthMbps);
    }

    private static VideoQuality Make(string name, int width, int height, int fps, int limit)
    {
        int mbps = Math.Clamp((int)Math.Round(width * (double)height * fps * 0.2 / 1_000_000), 4, 200);
        if (limit > 0)
            mbps = Math.Min(mbps, limit);
        return new VideoQuality(name, width, height, fps, mbps);
    }
}

// Flags sent by the client in the last byte of its login. Bit 0 keeps the old meaning (H.264 available).
internal static class SessionFlags
{
    internal const byte H264 = 1, Audio = 2, Input = 4, Quality = 8;
    internal const byte Known = H264 | Audio | Input | Quality;
}

// Merges the audio and input status lines into the single label of each page.
internal sealed class FeatureStatus(Action<string> publish)
{
    private readonly Dictionary<string, string> parts = new();

    internal void Set(string key, string text)
    {
        lock (parts)
        {
            parts[key] = text;
            publish(string.Join(" · ", parts.Values.Where(value => value.Length > 0)));
        }
    }

    internal void Clear()
    {
        lock (parts)
        {
            parts.Clear();
            publish("");
        }
    }
}

internal static class AudioFeed
{
    internal const byte Tag = (byte)'A';

    // Host: captures what the computer plays and sends it as 16-bit PCM.
    internal static async Task SendAsync(Stream stream, bool allowed, Action<string> status, CancellationToken stop,
        string? deviceId = null, int audioMode = 0, AudioApplication? application = null, bool standardFormat = false)
    {
        // Identify this side stream before audio initialization can fail. Video must still start.
        await stream.WriteAsync(new byte[] { Tag }, stop);
        if (!allowed)
        {
            await stream.WriteAsync(new byte[] { 0 }, stop);
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? chosen = null;
        if (audioMode == 0 && !string.IsNullOrEmpty(deviceId))
        {
            try { chosen = enumerator.GetDevice(deviceId); }
            catch (COMException ex) { Trace.Note($"Se usará la salida de audio predeterminada: {ex.Message}"); }
        }
        using MMDevice? deviceToDispose = chosen;
        IWaveIn selectedCapture;
        if (audioMode == 0)
            selectedCapture = chosen is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(chosen);
        else
            selectedCapture = await ProcessAudioCapture.CreateAsync(application ?? throw new IOException("No se eligió una aplicación de audio."),
                exclude: audioMode == 2, stop);
        using IWaveIn capture = selectedCapture;
        if (standardFormat && capture is WasapiLoopbackCapture)
            capture.WaveFormat = new WaveFormat(48000, 16, 2);
        WaveFormat source = capture.WaveFormat;
        bool floating = source.BitsPerSample == 32;
        if (!floating && source.BitsPerSample != 16)
        {
            await stream.WriteAsync(new byte[] { 0 }, stop);
            status("Audio: formato no compatible");
            return;
        }

        int channels = Math.Min(2, source.Channels);
        var header = new byte[6];
        header[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), source.SampleRate);
        header[5] = (byte)channels;
        await stream.WriteAsync(header, stop);

        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
        });
        capture.RecordingStopped += (_, e) => queue.Writer.TryComplete(e.Exception);
        capture.DataAvailable += (_, e) =>
        {
            int frames = e.BytesRecorded / source.BlockAlign;
            if (frames == 0)
                return;
            var pcm = new byte[frames * channels * 2];
            int offset = 0;
            for (int frame = 0; frame < frames; frame++)
                for (int channel = 0; channel < channels; channel++)
                {
                    int at = frame * source.BlockAlign + channel * (floating ? 4 : 2);
                    short sample = floating
                        ? (short)(Math.Clamp(BitConverter.ToSingle(e.Buffer, at), -1f, 1f) * 32767f)
                        : BitConverter.ToInt16(e.Buffer, at);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), sample);
                    offset += 2;
                }
            queue.Writer.TryWrite(pcm);
        };
        capture.StartRecording();
        string filter = audioMode switch { 1 => $" · solo {application!.Name}", 2 => $" · sin {application!.Name}", _ => "" };
        status($"Audio {source.SampleRate / 1000.0:0.#} kHz{filter}");
        try
        {
            await foreach (byte[] chunk in queue.Reader.ReadAllAsync(stop))
                await stream.WriteAsync(chunk, stop);
        }
        finally
        {
            try { capture.StopRecording(); }
            catch (InvalidOperationException ex) { Trace.Note($"La captura de audio ya estaba detenida: {ex.Message}"); }
        }
    }

    // Client: the tag byte was already read by the caller.
    internal static async Task ReceiveAsync(Stream stream, Action<string> status, CancellationToken stop)
    {
        var enabled = new byte[1];
        await stream.ReadExactlyAsync(enabled, stop);
        if (enabled[0] == 0)
        {
            status("El anfitrión no comparte audio");
            return;
        }

        var header = new byte[5];
        await stream.ReadExactlyAsync(header, stop);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(header);
        int channels = header[4];
        if (rate is < 8000 or > 192000 || channels is < 1 or > 2)
            throw new IOException("Formato de audio inválido.");

        var buffer = new PcmPlaybackBuffer(new WaveFormat(rate, 16, channels));
        using var output = new WasapiOut(AudioClientShareMode.Shared, 40);
        output.Init(buffer);
        output.Play();
        status("Audio activo");
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, stop)) != 0)
            buffer.AddSamples(chunk, 0, read);
    }
}

internal static class InputFeed
{
    // Identifies our SendInput messages so a client on the same PC does not send them back.
    internal const nint InjectedInputTag = 0x52504C59;
    internal static bool IsOwnInputMessage => GetMessageExtraInfo() == InjectedInputTag;
    internal const byte Hello = (byte)'I';
    internal const int PacketSize = 8;
    internal const byte Move = 1, Button = 2, Wheel = 3, Key = 4, ReleaseAll = 5;
    private const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseWheel = 0x0800, MouseHWheel = 0x1000;
    private static readonly Lock InputGate = new();
    private static readonly Dictionary<(ushort Key, bool Extended), int> HeldKeys = [];
    private static readonly Dictionary<byte, int> HeldButtons = [];

    // type, flags, then two 16-bit values.
    internal static byte[] Packet(byte type, byte flags = 0, ushort a = 0, ushort b = 0)
    {
        var packet = new byte[PacketSize];
        packet[0] = type;
        packet[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), a);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), b);
        return packet;
    }

    // Client: asks the host for permission, then forwards queued packets.
    internal static async Task ClientAsync(Stream stream, ChannelReader<byte[]> queue,
        Action<string> status, CancellationToken stop, Action<bool>? inputState, bool managed)
    {
        var grant = new byte[1];
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(new byte[] { Hello }, wait.Token);
            await stream.ReadExactlyAsync(grant, wait.Token);
        }

        if (grant[0] != 1)
        {
            status("El anfitrión no permite mouse ni teclado");
            await foreach (byte[] _ in queue.ReadAllAsync(stop)) { /* Discard input after the host declines it. */ }
            return;
        }

        status(managed ? "Permisos de entrada gestionados por el anfitrión" : "Mouse y teclado activos");
        if (!managed)
            inputState?.Invoke(true);
        try
        {
            await foreach (byte[] queued in queue.ReadAllAsync(stop))
            {
                byte[] packet = queued;
                // Keep the newest move, without crossing a button, key or release packet.
                while (packet[0] == Move && queue.TryPeek(out byte[]? next) && next[0] == Move && queue.TryRead(out var newer))
                    packet = newer;
                await stream.WriteAsync(packet, stop);
            }
        }
        finally
        {
            if (!managed)
                inputState?.Invoke(false);
        }
    }

    // Host: injects what the guest sends, only when the host allowed it.
    internal static async Task HostAsync(Stream stream, GuestSession guest, bool managed, Action<string> status, CancellationToken stop,
        IdleTracker? idle = null)
    {
        bool allowed;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = new byte[1];
            await stream.ReadExactlyAsync(hello, handshake.Token);
            if (hello[0] != Hello)
                throw new IOException("Canal de entrada inválido.");
            lock (guest.Gate)
                allowed = managed || (guest.Permissions & GuestDevices.Desktop) != GuestDevices.None;
            await stream.WriteAsync(new byte[] { (byte)(allowed ? 1 : 0) }, handshake.Token);
        }
        if (!allowed)
            return;

        status("Canal de entrada de invitado abierto");
        var keys = new Dictionary<ushort, bool>();
        var buttons = new HashSet<byte>();
        var packet = new byte[PacketSize];
        var pointer = new RemotePointer();
        HostMousePriority? priority = guest.MousePriority;
        if (priority is not null) priority.Activated += PauseMouse;
        lock (guest.Gate)
        {
            guest.InputConnected = true;
            guest.Revoked += Revoke;
        }
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(packet, stop);
                lock (guest.Gate)
                {
                    GuestDevices device = packet[0] switch
                    {
                        Key => GuestDevices.Keyboard, Move or Button or Wheel => GuestDevices.Mouse, _ => GuestDevices.None
                    };
                    if (packet[0] == ReleaseAll || (guest.Permissions & device) != GuestDevices.None)
                    {
                        lock (InputGate)
                        {
                            if (device == GuestDevices.Mouse && !pointer.Accept(packet[0],
                                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)),
                                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)), priority))
                            {
                                if (priority?.Active == true)
                                    Release(keys, buttons, GuestDevices.Mouse, priority);
                                continue;
                            }
                            idle?.Touch();
                            if ((packet[0] == Wheel || (packet[0] == Button && (packet[1] & 0x80) != 0)) && pointer.Position is { } at)
                                Send(MouseInput(MouseMove | MouseAbsolute, 0, at.X, at.Y));
                            Apply(packet, keys, buttons, priority);
                        }
                    }
                }
            }
        }
        finally
        {
            if (priority is not null) priority.Activated -= PauseMouse;
            lock (guest.Gate)
            {
                guest.InputConnected = false;
                guest.Revoked -= Revoke;
                Revoke(GuestDevices.Desktop);
            }
        }

        void Revoke(GuestDevices devices)
        {
            lock (InputGate)
                Release(keys, buttons, devices, priority);
        }

        void PauseMouse()
        {
            lock (InputGate)
                if (priority?.Active == true)
                    Release(keys, buttons, GuestDevices.Mouse, priority);
        }
    }

    private static void Apply(byte[] packet, Dictionary<ushort, bool> keys, HashSet<byte> buttons, HostMousePriority? priority)
    {
        ushort a = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        ushort b = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
        switch (packet[0])
        {
            case Move:
                Send(MouseInput(MouseMove | MouseAbsolute, 0, a, b));
                break;
            case Button:
            {
                byte id = (byte)(packet[1] & 0x7F);
                bool down = (packet[1] & 0x80) != 0;
                if (id > 4)
                    break;
                if (down && buttons.Add(id) && AddHolder(HeldButtons, id))
                    Send(ButtonInput(id, true));
                else if (!down && buttons.Remove(id))
                    ReleaseButton(id);
                break;
            }
            case Wheel:
                Send(MouseInput(packet[1] == 1 ? MouseHWheel : MouseWheel, unchecked((uint)(int)(short)a), 0, 0));
                break;
            case Key:
            {
                if (a is 0 or > 254)
                    break;
                bool up = (packet[1] & 1) != 0;
                bool extended = (packet[1] & 2) != 0;
                if (up)
                {
                    if (keys.Remove(a, out bool heldExtended))
                        ReleaseKey(a, heldExtended);
                }
                else if (keys.TryGetValue(a, out bool repeating))
                    Send(KeyInput(a, false, repeating));
                else
                {
                    keys[a] = extended;
                    if (AddHolder(HeldKeys, (a, extended)))
                        Send(KeyInput(a, false, extended));
                }
                break;
            }
            case InputFeed.ReleaseAll:
                Release(keys, buttons, GuestDevices.Desktop, priority);
                break;
        }
    }

    private static bool AddHolder<T>(Dictionary<T, int> holders, T key) where T : notnull
    {
        int count = holders.GetValueOrDefault(key);
        holders[key] = count + 1;
        return count == 0;
    }

    private static bool RemoveHolder<T>(Dictionary<T, int> holders, T key) where T : notnull
    {
        if (holders[key] == 1)
            return holders.Remove(key);
        holders[key]--;
        return false;
    }

    private static void Release(Dictionary<ushort, bool> keys, HashSet<byte> buttons, GuestDevices devices, HostMousePriority? priority = null)
    {
        if (devices.HasFlag(GuestDevices.Keyboard))
        {
            foreach (var (key, extended) in keys)
                ReleaseKey(key, extended);
            keys.Clear();
        }
        if (devices.HasFlag(GuestDevices.Mouse))
        {
            foreach (byte button in buttons)
                ReleaseButton(button, priority);
            buttons.Clear();
        }
    }

    private static void ReleaseKey(ushort key, bool extended)
    {
        if (RemoveHolder(HeldKeys, (key, extended)))
            Send(KeyInput(key, true, extended));
    }

    private static void ReleaseButton(byte button, HostMousePriority? priority = null)
    {
        if (RemoveHolder(HeldButtons, button) && priority?.IsPhysicallyHeld(button) != true)
            Send(ButtonInput(button, false));
    }

    private static NativeInput ButtonInput(byte id, bool down)
    {
        (uint flag, uint data) = (id, down) switch
        {
            (0, true) => (0x0002u, 0u), (0, false) => (0x0004u, 0u),
            (1, true) => (0x0008u, 0u), (1, false) => (0x0010u, 0u),
            (2, true) => (0x0020u, 0u), (2, false) => (0x0040u, 0u),
            (3, true) => (0x0080u, 1u), (3, false) => (0x0100u, 1u),
            (_, true) => (0x0080u, 2u), _ => (0x0100u, 2u)
        };
        return MouseInput(flag, data, 0, 0);
    }

    private static NativeInput MouseInput(uint flags, uint data, int x, int y) => new()
    {
        type = 0, union = new InputUnion { mouse = new MouseData { dx = x, dy = y, data = data, flags = flags, extra = InjectedInputTag } }
    };

    private static NativeInput KeyInput(ushort key, bool up, bool extended) => new()
    {
        type = 1,
        union = new InputUnion
        {
            key = new KeyData
            {
                vk = key, scan = GetScanCode(key, extended), flags = (up ? 2u : 0u) | (extended ? 1u : 0u), extra = InjectedInputTag
            }
        }
    };

    private static void Send(NativeInput input) => SendInput(1, ref input, Marshal.SizeOf<NativeInput>());

    private static ushort GetScanCode(ushort key, bool extended) =>
        (ushort)(MapVirtualKeyW(key, extended ? 4u : 0u) & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public int dx, dy; public uint data, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyData { public ushort vk, scan; public uint flags, time; public nint extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseData mouse;
        [FieldOffset(0)] public KeyData key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint type; public InputUnion union; }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SendInput(uint count, ref NativeInput input, int size);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetMessageExtraInfo();
}
