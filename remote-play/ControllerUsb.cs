using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemotePlay;

// Windows inbox HID/WinUSB APIs. No driver replacement or external controller program.
internal static class ControllerUsb
{
    internal sealed record Device(string Path, int ReportLength, string Parent);
    private sealed record InterfacePath(string Path, string Parent);
    private static readonly Guid VendorInterface = new("6f13725e-ef0e-4fd3-ae5f-b2de989ec825");
    internal static Device[] FindPro2()
    {
        HidD_GetHidGuid(out Guid guid);
        var devices = new List<Device>();
        foreach (InterfacePath candidate in Paths(guid))
        {
            string path = candidate.Path;
            if (!path.Contains("vid_057e&pid_2069&mi_00", StringComparison.OrdinalIgnoreCase)) continue;
            using SafeFileHandle handle = Open(path, 0, false);
            if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out nint data)) continue;
            try
            {
                if (HidP_GetCaps(data, out HidCaps caps) == 0x00110000 && caps.InputLength is >= 12 and <= 1024)
                    devices.Add(new(path, caps.InputLength, candidate.Parent));
            }
            finally { HidD_FreePreparsedData(data); }
        }
        return devices.ToArray();
    }

    internal static SafeFileHandle Open(string path, uint access, bool asynchronous) =>
        CreateFileW(path, access, 3, 0, 3, asynchronous ? 0x40000000u : 0, 0);

    internal static bool WakePro2(Device device)
    {
        // HID and vendor interfaces have different instance IDs; match their USB parent.
        if (device.Parent.Length == 0) return false;
        string? path = Paths(VendorInterface).FirstOrDefault(candidate =>
            candidate.Path.Contains("vid_057e&pid_2069&mi_01", StringComparison.OrdinalIgnoreCase) &&
            candidate.Parent.Equals(device.Parent, StringComparison.OrdinalIgnoreCase))?.Path;
        if (path is null) return false;
        using SafeFileHandle file = Open(path, 0xC0000000, true);
        if (file.IsInvalid || !WinUsb_Initialize(file, out nint usb)) return false;
        try
        {
            if (!WinUsb_QueryInterfaceSettings(usb, 0, out UsbInterface descriptor)) return false;
            for (byte i = 0; i < descriptor.Endpoints; i++)
            {
                if (!WinUsb_QueryPipe(usb, 0, i, out UsbPipe pipe) || pipe.Type != 2 || (pipe.Id & 0x80) != 0) continue;
                uint timeout = 150;
                if (!WinUsb_SetPipePolicy(usb, pipe.Id, 3, sizeof(uint), ref timeout)) return false;
                byte[][] commands =
                [
                    [3, 0x91, 0, 0x0d, 0, 8, 0, 0, 1, 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
                    [7, 0x91, 0, 1, 0, 0, 0, 0], [0x16, 0x91, 0, 1, 0, 0, 0, 0],
                    [3, 0x91, 0, 0x0a, 0, 4, 0, 0, 9, 0, 0, 0],
                    [9, 0x91, 0, 7, 0, 8, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]
                ];
                foreach (byte[] command in commands)
                {
                    if (!WinUsb_WritePipe(usb, pipe.Id, command, (uint)command.Length, out uint written, 0) || written != command.Length)
                        return false;
                    Thread.Sleep(20); // Device initialization only; never on the input/video path.
                }
                return true;
            }
            return false;
        }
        finally { WinUsb_Free(usb); }
    }

    private static IEnumerable<InterfacePath> Paths(Guid guid)
    {
        nint devices = SetupDiGetClassDevsW(in guid, null, 0, 0x12);
        if (devices == -1) yield break;
        try
        {
            for (uint i = 0; i < 256; i++)
            {
                var info = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(devices, 0, in guid, i, ref info)) yield break;
                SetupDiGetDeviceInterfaceDetailW(devices, ref info, 0, 0, out uint needed, ref device);
                if (needed is < 8 or > 65536) continue;
                nint detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(devices, ref info, detail, needed, out _, ref device) &&
                        Marshal.PtrToStringUni(detail + 4) is { } path) yield return new(path, PhysicalParent(device.Node));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    private static string PhysicalParent(uint node)
    {
        var text = new StringBuilder(1024);
        for (int i = 0; i < 6; i++)
        {
            text.Clear();
            if (CM_Get_Device_IDW(node, text, text.Capacity, 0) == 0 &&
                text.ToString().StartsWith("USB\\VID_057E&PID_2069\\", StringComparison.OrdinalIgnoreCase)) return text.ToString();
            if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
            node = parent;
        }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint Node; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public ushort[] Counts;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterface { public byte Length, Type, Number, Alternate, Endpoints, Class, Subclass, Protocol, String; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UsbPipe { public int Type; public byte Id; public ushort MaxPacket; public byte Interval; }

    [DllImport("hid.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiGetClassDevsW(in Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint data, in Guid guid, uint index, ref InterfaceData info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(nint devices, ref InterfaceData info, nint detail, uint size, out uint needed, ref DeviceInfo data);
    [DllImport("setupapi.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_IDW(uint node, StringBuilder text, int size, uint flags);
    [DllImport("cfgmgr32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_Initialize(SafeFileHandle file, out nint usb);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_Free(nint usb);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_QueryInterfaceSettings(nint usb, byte alternate, out UsbInterface descriptor);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_QueryPipe(nint usb, byte alternate, byte index, out UsbPipe pipe);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_SetPipePolicy(nint usb, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinUsb_WritePipe(nint usb, byte pipe, byte[] data, uint length, out uint written, nint overlapped);
}
