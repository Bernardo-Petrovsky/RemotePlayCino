using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePlay;

// Shared by all guests; never blocks the Windows hook on a session/input lock.
internal sealed class HostMousePriority(Func<long>? clock = null)
{
    internal const int GraceMilliseconds = 1000;
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long until;
    private int physicalButtons;
    internal bool Active => Volatile.Read(ref physicalButtons) != 0 || now() < Volatile.Read(ref until);
    internal event Action? Activated;
    internal bool IsPhysicallyHeld(byte button) => (Volatile.Read(ref physicalButtons) & (1 << button)) != 0;

    internal void PhysicalActivity(int buttons)
    {
        bool wasActive = Active;
        Volatile.Write(ref physicalButtons, buttons);
        Volatile.Write(ref until, now() + GraceMilliseconds);
        if (!wasActive)
            ThreadPool.QueueUserWorkItem(_ => Activated?.Invoke());
    }
}

// Caches even suppressed positions, so an idle guest cannot restore an old position.
internal sealed class RemotePointer
{
    private (ushort X, ushort Y)? position;
    internal (ushort X, ushort Y)? Position => position;

    internal bool Accept(byte type, ushort x, ushort y, HostMousePriority? priority)
    {
        bool changed = position != (x, y);
        if (type == InputFeed.Move) position = (x, y);
        return priority?.Active != true && (type != InputFeed.Move || changed);
    }
}

internal sealed class HostMouseMonitor : IDisposable
{
    private readonly HookProc callback;
    private readonly HostMousePriority priority;
    private readonly Func<Point, bool> isLocalViewer;
    private nint hook;
    private int buttons;

    internal HostMouseMonitor(HostMousePriority priority, Func<Point, bool> isLocalViewer)
    {
        this.priority = priority;
        this.isLocalViewer = isLocalViewer;
        callback = OnMouse;
        hook = SetWindowsHookExW(14, callback, GetModuleHandleW(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo activar la prioridad del mouse local.");
    }

    internal static bool IsPhysical(uint flags, nuint extra) =>
        (flags & 3) == 0 && extra != unchecked((nuint)InputFeed.InjectedInputTag);

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<MouseHookData>(data);
            if (IsPhysical(input.flags, input.extra))
                RecordPhysical((int)message, input.mouseData, new Point(input.x, input.y));
        }
        return CallNextHookEx(hook, code, message, data);
    }

    internal void RecordPhysical(int message, uint mouseData, Point point)
    {
        bool localViewer = isLocalViewer(point);
        int id = message switch
        {
            0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2,
            0x20B or 0x20C => (mouseData >> 16) == 1 ? 3 : 4, _ => -1
        };
        bool down = message is 0x201 or 0x204 or 0x207 or 0x20B;
        bool releasingHostButton = id >= 0 && !down && (buttons & (1 << id)) != 0;
        if (id >= 0)
        {
            if (!down) buttons &= ~(1 << id);
            else if (!localViewer) buttons |= 1 << id;
        }
        // A host drag may end over our own local viewer; its release must not get lost.
        if (!localViewer || buttons != 0 || releasingHostButton)
            priority.PhysicalActivity(buttons);
    }

    public void Dispose()
    {
        nint previous = Interlocked.Exchange(ref hook, 0);
        if (previous != 0) UnhookWindowsHookEx(previous);
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData { public int x, y; public uint mouseData, flags, time; public nuint extra; }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetWindowsHookExW(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetModuleHandleW(string? name);
}
