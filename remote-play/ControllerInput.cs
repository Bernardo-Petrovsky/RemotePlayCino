using System.Security.Cryptography;
using System.Text;

namespace RemotePlay;

internal abstract class ControllerDevice(string key, string name, bool nintendo, int playerIndex) : IDisposable
{
    internal string Key { get; } = key;
    internal string Name { get; } = name;
    internal bool Nintendo { get; } = nintendo;
    internal int PlayerIndex { get; } = playerIndex;
    internal abstract PadState? Poll();
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    protected abstract void Dispose(bool disposing);
}

internal sealed class ControllerInput : IDisposable
{
    internal sealed record Snapshot(ControllerDevice[] Devices, string? Error, long Revision);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, ControllerDevice> devices = [];
    private readonly bool sdlReady;
    private readonly Task discovery;
    private readonly Lock lifetime = new();
    private Snapshot snapshot = new([], null, 0);
    private ControllerDevice? selected;
    private int letters;
    private Task? shutdown;
    private bool disposed;
    private readonly string? initializationError;
    internal Snapshot Current => Volatile.Read(ref snapshot);
    internal void Select(ControllerDevice? device) => Volatile.Write(ref selected, device);
    internal void SetNintendoLetters(bool enabled) => Volatile.Write(ref letters, enabled ? 1 : 0);

    // Must be constructed and disposed on the main thread, as required by SDL initialization.
    internal ControllerInput()
    {
        try
        {
            SdlGamepads.SDL_SetMainReady();
            SdlGamepads.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SdlGamepads.SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
            // The Windows USB 09-report reader below handles Pro 2 without libusb or driver changes.
            SdlGamepads.SDL_SetHint("SDL_JOYSTICK_HIDAPI_SWITCH2", "0");
            sdlReady = SdlGamepads.SDL_InitSubSystem(SdlGamepads.InitGamepad);
            if (!sdlReady) initializationError = SdlGamepads.Error;
            else
            {
                SdlGamepads.SDL_SetGamepadEventsEnabled(false);
                SdlGamepads.SDL_SetJoystickEventsEnabled(false);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            initializationError = "Detección directa no disponible: conserva SDL3.dll junto al ejecutable.";
        }
        discovery = Task.Run(DiscoverAsync, CancellationToken.None); // The worker owns cleanup even if cancellation precedes scheduling.
    }

    internal PadState? Poll()
    {
        ControllerDevice? device = Volatile.Read(ref selected);
        PadState? state = device?.Poll();
        return state is { } pad && device?.Nintendo == true && Volatile.Read(ref letters) != 0 ? SwapNintendo(pad) : state;
    }
    internal static PadState SwapNintendo(PadState state)
    {
        ushort bits = state.Buttons;
        return state with { Buttons = (ushort)((bits & 0x0fff) | ((bits & 0x1000) << 1) | ((bits & 0x2000) >> 1) |
            ((bits & 0x4000) << 1) | ((bits & 0x8000) >> 1)) };
    }

    private async Task DiscoverAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { Scan(); }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                { Publish(ex.Message); }
                await Task.Delay(750, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { /* App close. */ }
        finally
        {
            Volatile.Write(ref selected, null);
            foreach (ControllerDevice device in devices.Values) device.Dispose();
            devices.Clear();
            Publish(null);
        }
    }

    private void Scan()
    {
        string? error = initializationError;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (ControllerUsb.Device usb in ControllerUsb.FindPro2())
        {
            string key = Key("pro2", usb.Path);
            present.Add(key);
            if (devices.TryGetValue(key, out var old) && old is Switch2ProPad { Alive: false })
            { devices.Remove(key); old.Dispose(); }
            if (!devices.ContainsKey(key))
            {
                try { devices.Add(key, new Switch2ProPad(usb, key)); }
                catch (IOException ex) { error = ex.Message; }
            }
        }
        if (sdlReady)
        {
            SdlGamepads.SDL_UpdateGamepads();
            foreach (uint id in SdlGamepads.GetIds())
            {
                ushort vendor = SdlGamepads.SDL_GetGamepadVendorForID(id), product = SdlGamepads.SDL_GetGamepadProductForID(id);
                if (vendor == 0x057e && product == 0x2069) continue;
                int type = SdlGamepads.SDL_GetGamepadTypeForID(id), player = SdlGamepads.SDL_GetGamepadPlayerIndexForID(id);
                if (type is 2 or 3 && VirtualPad.IsOwnSlot(player)) continue;
                string key = Key($"sdl:{vendor:x4}:{product:x4}", SdlGamepads.DevicePath(id));
                present.Add(key);
                if (devices.TryGetValue(key, out var old) && old is SdlPad existing && existing.InstanceId != id)
                { devices.Remove(key); old.Dispose(); }
                if (devices.ContainsKey(key)) continue;
                string name = SdlGamepads.Name(id);
                if (vendor == 0x054c && type == 5) name = "Sony DualShock 4";
                if (name.Length == 0) name = "Mando compatible";
                var device = new SdlPad(id, key, name, vendor == 0x057e || type is >= 7 and <= 10, type is 2 or 3 ? player : -1);
                if (device.Opened) devices.Add(key, device);
                else device.Dispose();
            }
        }
        else
        {
            for (int slot = 0; slot < 4; slot++)
            {
                string key = $"xinput:{slot}";
                if (VirtualPad.IsOwnSlot(slot) || XInput.Poll(slot) is null) continue;
                present.Add(key);
                if (!devices.ContainsKey(key)) devices.Add(key, new XInputDevice(slot));
            }
        }
        foreach (string key in devices.Keys.Where(key => !present.Contains(key)).ToArray())
        { ControllerDevice old = devices[key]; devices.Remove(key); old.Dispose(); }
        Publish(error);
    }

    private void Publish(string? error)
    {
        ControllerDevice[] current = devices.Values.OrderByDescending(device => device.Nintendo)
            .ThenBy(device => device.Name, StringComparer.Ordinal).ThenBy(device => device.Key, StringComparer.Ordinal).ToArray();
        Snapshot previous = Current;
        if (previous.Error == error && current.SequenceEqual(previous.Devices)) return;
        Volatile.Write(ref snapshot, new(current, error, previous.Revision + 1));
    }
    private static string Key(string type, string path) =>
        type + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];

    internal Task StopAsync()
    {
        lock (lifetime) return shutdown ??= ShutdownAsync();
    }
    private async Task ShutdownAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await discovery.ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAsync().GetAwaiter().GetResult();
        if (sdlReady) { SdlGamepads.SDL_QuitSubSystem(SdlGamepads.InitGamepad); SdlGamepads.SDL_Quit(); }
        stop.Dispose();
    }

    private sealed class XInputDevice(int slot) : ControllerDevice($"xinput:{slot}", $"Mando compatible · XInput {slot + 1}", false, slot)
    {
        internal override PadState? Poll() => VirtualPad.IsOwnSlot(PlayerIndex) ? null : XInput.Poll(PlayerIndex);
        protected override void Dispose(bool disposing) { /* No native handle. */ }
    }
}
