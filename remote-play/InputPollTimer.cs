using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace RemotePlay;

internal sealed class InputPollTimer : IDisposable
{
    private readonly PeriodicTimer? fallback;
    private readonly TimerSignal? signal;
    private readonly RegisteredWaitHandle? registration;
    private readonly Channel<bool>? ticks;

    internal InputPollTimer(bool precise)
    {
        int period = precise ? 8 : 16;
        if (precise)
        {
            const uint highResolution = 2;
            const uint synchronizeAndModify = 0x00100002;
            SafeWaitHandle timer = CreateWaitableTimerExW(IntPtr.Zero, null, highResolution, synchronizeAndModify);
            long due = -period * TimeSpan.TicksPerMillisecond;
            if (!timer.IsInvalid && SetWaitableTimer(timer, in due, period, IntPtr.Zero, IntPtr.Zero, false))
            {
                signal = new TimerSignal(timer);
                ticks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
                try
                {
                    registration = ThreadPool.UnsafeRegisterWaitForSingleObject(signal,
                        static (state, _) => ((ChannelWriter<bool>)state!).TryWrite(true), ticks.Writer,
                        Timeout.Infinite, executeOnlyOnce: false);
                }
                catch
                {
                    signal.Dispose();
                    throw;
                }
                return;
            }
            timer.Dispose();
        }
        fallback = new PeriodicTimer(TimeSpan.FromMilliseconds(period));
    }

    internal ValueTask<bool> WaitAsync(CancellationToken stop) => ticks is null
        ? fallback!.WaitForNextTickAsync(stop)
        : ticks.Reader.ReadAsync(stop);

    public void Dispose()
    {
        registration?.Unregister(null);
        ticks?.Writer.TryComplete();
        signal?.Dispose();
        fallback?.Dispose();
    }

    private sealed class TimerSignal : WaitHandle
    {
        internal TimerSignal(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long due, int period, IntPtr completion,
        IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
