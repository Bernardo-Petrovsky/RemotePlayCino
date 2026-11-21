using System.Diagnostics;

namespace RemotePlay;

internal sealed class Switch2ProPad : ControllerDevice
{
    private sealed record Sample(PadState State, long Time);
    private readonly CancellationTokenSource stop = new();
    private readonly FileStream stream;
    private readonly Task reading;
    private Sample? latest;
    private int disposed;
    internal bool Alive => !reading.IsCompleted;

    internal Switch2ProPad(ControllerUsb.Device device, string key) : base(key, "Nintendo Switch 2 Pro Controller · USB", true, -1)
    {
        ControllerUsb.WakePro2(device);
        var handle = ControllerUsb.Open(device.Path, 0x80000000, true);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("No se pudo abrir el Pro Controller de Switch 2."); }
        try { stream = new FileStream(handle, FileAccess.Read, device.ReportLength, isAsync: true); }
        catch { handle.Dispose(); throw; }
        reading = ReadAsync(device.ReportLength);
    }

    internal override PadState? Poll()
    {
        Sample? value = Volatile.Read(ref latest);
        return value is not null && Stopwatch.GetElapsedTime(value.Time).TotalMilliseconds < 250 ? value.State : null;
    }
    private async Task ReadAsync(int reportLength)
    {
        var report = new byte[reportLength];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int length = await stream.ReadAsync(report, stop.Token).ConfigureAwait(false);
                if (length == 0) break;
                if (Parse(report.AsSpan(0, length)) is { } state)
                    Volatile.Write(ref latest, new(state, Stopwatch.GetTimestamp()));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { /* Closing or unplugging. */ }
        catch (IOException) { /* Removal: neutral state until the next device discovery. */ }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { /* Closing a pending HID read. */ }
        finally { Volatile.Write(ref latest, null); }
    }

    // Report 09 and axis ranges match the existing MIT-licensed wired_pro2 reader.
    internal static PadState? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data[0] != 9) return null;
        ushort buttons = 0;
        if ((data[3] & 1) != 0) buttons |= (ushort)PadButtons.A; // Nintendo B: south position.
        if ((data[3] & 2) != 0) buttons |= (ushort)PadButtons.B;
        if ((data[3] & 4) != 0) buttons |= (ushort)PadButtons.X;
        if ((data[3] & 8) != 0) buttons |= (ushort)PadButtons.Y;
        if ((data[3] & 0x10) != 0) buttons |= (ushort)PadButtons.RB;
        if ((data[3] & 0x40) != 0) buttons |= (ushort)PadButtons.Start;
        if ((data[3] & 0x80) != 0) buttons |= (ushort)PadButtons.RS;
        if ((data[4] & 1) != 0) buttons |= (ushort)PadButtons.Down;
        if ((data[4] & 2) != 0) buttons |= (ushort)PadButtons.Right;
        if ((data[4] & 4) != 0) buttons |= (ushort)PadButtons.Left;
        if ((data[4] & 8) != 0) buttons |= (ushort)PadButtons.Up;
        if ((data[4] & 0x10) != 0) buttons |= (ushort)PadButtons.LB;
        if ((data[4] & 0x40) != 0) buttons |= (ushort)PadButtons.Back;
        if ((data[4] & 0x80) != 0) buttons |= (ushort)PadButtons.LS;
        int lx = data[6] | ((data[7] & 15) << 8), ly = (data[7] >> 4) | (data[8] << 4);
        int rx = data[9] | ((data[10] & 15) << 8), ry = (data[10] >> 4) | (data[11] << 4);
        return new(buttons, (byte)((data[4] & 0x20) != 0 ? 255 : 0), (byte)((data[3] & 0x20) != 0 ? 255 : 0),
            Axis(lx, 437, 3582), Axis(ly, 661, 3670), Axis(rx, 628, 3656), Axis(ry, 471, 3626));
    }
    private static short Axis(int raw, int low, int high)
    {
        double value = Math.Clamp(2.0 * (raw - low) / (high - low) - 1, -1, 1);
        return Math.Abs(value) < .08 ? (short)0 : (short)Math.Round(value * short.MaxValue);
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        stream.Dispose();
        reading.GetAwaiter().GetResult();
        stop.Dispose();
    }
}
