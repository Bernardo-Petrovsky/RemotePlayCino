using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemotePlay;

// Puts every ffmpeg child in a Windows job that kills them when this app exits or crashes.
internal static class ChildJob
{
    private const uint KillOnClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private static readonly Lazy<SafeFileHandle> Job = new(Create);

    internal static void EnsureAvailable() => _ = Job.Value;

    private static SafeFileHandle Create()
    {
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Windows no pudo crear el grupo de procesos de FFmpeg.");
        }

        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnClose } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, in info, (uint)Marshal.SizeOf<ExtendedLimits>()))
            return job;

        int limitError = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(limitError, "Windows no pudo activar el cierre automático de FFmpeg.");
    }

    internal static void Add(Process process)
    {
        if (!AssignProcessToJobObject(Job.Value, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo registrar el proceso de FFmpeg.");
        LocalStreamPriority.TrackChild(process);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, in ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
