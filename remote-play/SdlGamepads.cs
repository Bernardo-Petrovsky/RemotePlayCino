using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemotePlay;

internal static class SdlGamepads
{
    private const string Library = "RemotePlay.SDL3";
    private static readonly Lazy<nint> Native = new(() => NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "SDL3.dll")));
    internal const uint InitGamepad = 0x2000;
    static SdlGamepads() => NativeLibrary.SetDllImportResolver(typeof(SdlGamepads).Assembly,
        (name, _, _) => name == Library ? Native.Value : 0);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_SetMainReady();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_InitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_Quit();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
    internal static string Error => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "SDL no pudo leer el mando";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_SetGamepadEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_SetJoystickEventsEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_UpdateGamepads();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_LockJoysticks();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_UnlockJoysticks();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepads(out int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(nint memory);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_OpenGamepad(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_CloseGamepad(nint gamepad);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GamepadConnected(SafeGamepadHandle gamepad);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepadNameForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepadPathForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort SDL_GetGamepadVendorForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort SDL_GetGamepadProductForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_GetGamepadTypeForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_GetGamepadPlayerIndexForID(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetGamepadButton(SafeGamepadHandle gamepad, int button);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern short SDL_GetGamepadAxis(SafeGamepadHandle gamepad, int axis);

    internal static string Name(uint id)
    {
        string name = GuestSession.CleanName(Marshal.PtrToStringUTF8(SDL_GetGamepadNameForID(id)));
        return name[..Math.Min(120, name.Length)];
    }
    internal static string DevicePath(uint id) => Marshal.PtrToStringUTF8(SDL_GetGamepadPathForID(id)) ?? $"instance:{id}";
    internal static uint[] GetIds()
    {
        nint array = SDL_GetGamepads(out int count);
        if (array == 0) throw new IOException(Error);
        try
        {
            if (count is < 0 or > 256) throw new IOException("Lista de mandos inválida");
            var ids = new uint[count];
            for (int i = 0; i < count; i++) ids[i] = unchecked((uint)Marshal.ReadInt32(array, i * sizeof(uint)));
            return ids;
        }
        finally { SDL_free(array); }
    }
}

internal sealed class SdlPad(uint id, string key, string name, bool nintendo, int playerIndex) : ControllerDevice(key, name, nintendo, playerIndex)
{
    private readonly SafeGamepadHandle handle = new(SdlGamepads.SDL_OpenGamepad(id));
    internal uint InstanceId { get; } = id;
    internal bool Opened => !handle.IsInvalid;
    internal override PadState? Poll()
    {
        try
        {
            SdlGamepads.SDL_LockJoysticks();
            try
            {
                SdlGamepads.SDL_UpdateGamepads();
                if (!SdlGamepads.SDL_GamepadConnected(handle)) return null;
                uint buttons = 0;
                for (int i = 0; i < 15; i++)
                    if (SdlGamepads.SDL_GetGamepadButton(handle, i)) buttons |= 1u << i;
                return Map(buttons, SdlGamepads.SDL_GetGamepadAxis(handle, 0), SdlGamepads.SDL_GetGamepadAxis(handle, 1),
                    SdlGamepads.SDL_GetGamepadAxis(handle, 2), SdlGamepads.SDL_GetGamepadAxis(handle, 3),
                    SdlGamepads.SDL_GetGamepadAxis(handle, 4), SdlGamepads.SDL_GetGamepadAxis(handle, 5));
            }
            finally { SdlGamepads.SDL_UnlockJoysticks(); }
        }
        catch (ObjectDisposedException) { return null; }
    }

    // SDL uses button positions and Y-down axes; the existing wire format uses XInput Y-up.
    internal static PadState Map(uint buttons, short lx, short ly, short rx, short ry, short lt, short rt)
    {
        ReadOnlySpan<ushort> bits = [0x1000, 0x2000, 0x4000, 0x8000, 0x20, 0, 0x10, 0x40, 0x80, 0x100, 0x200, 1, 2, 4, 8];
        ushort mapped = 0;
        for (int i = 0; i < bits.Length; i++) if ((buttons & (1u << i)) != 0) mapped |= bits[i];
        return new(mapped, Trigger(lt), Trigger(rt), lx, Invert(ly), rx, Invert(ry));
    }
    private static byte Trigger(short value) => (byte)(Math.Max(0, (int)value) * 255 / short.MaxValue);
    private static short Invert(short value) => value == short.MinValue ? short.MaxValue : (short)-value;
    protected override void Dispose(bool disposing) { if (disposing) handle.Dispose(); }
}

internal sealed class SafeGamepadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGamepadHandle(nint pointer) : base(true) => SetHandle(pointer);
    protected override bool ReleaseHandle() { SdlGamepads.SDL_CloseGamepad(handle); return true; }
}
