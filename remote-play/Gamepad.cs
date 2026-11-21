using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360.Exceptions;

namespace RemotePlay;

[Flags]
internal enum PadButtons : ushort
{
    None = 0, Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LS = 0x0040, RS = 0x0080, LB = 0x0100, RB = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PadState(
    ushort Buttons, byte LT, byte RT, short LX, short LY, short RX, short RY)
{
    // XInput button bits. Guide and the other reserved bits are excluded.
    internal const ushort ButtonMask = 0xF3FF;
    internal const int PacketSize = 13;

    internal static void Write(byte[] buffer, PadState? state)
    {
        buffer[0] = state.HasValue ? (byte)1 : (byte)0;
        PadState pad = state.GetValueOrDefault();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1), pad.Buttons);
        buffer[3] = pad.LT;
        buffer[4] = pad.RT;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(5), pad.LX);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(7), pad.LY);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(9), pad.RX);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(11), pad.RY);
    }

    internal static PadState? Read(byte[] buffer) => buffer[0] switch
    {
        0 => null,
        1 => new PadState(
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(1)) & ButtonMask),
            buffer[3], buffer[4],
            BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(5)),
            BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(7)),
            BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(9)),
            BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(11))),
        _ => throw new IOException("Informe de mando inválido.")
    };
}

internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        internal uint PacketNumber;
        internal PadState Pad;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState(uint index, out State state);

    internal static PadState? Poll(int index)
    {
        if (index is < 0 or > 3 || XInputGetState((uint)index, out var state) != 0)
            return null;
        return state.Pad with { Buttons = (ushort)(state.Pad.Buttons & PadState.ButtonMask) };
    }
}

internal sealed class VirtualPad : IDisposable
{
    private static int ownSlots;
    private static readonly Lock OwnGate = new();
    private static readonly HashSet<VirtualPad> Outputs = [];
    private int ownSlot = -1;
    internal static bool IsOwnSlot(int slot)
    {
        if (slot is < 0 or > 3) return false;
        if ((Volatile.Read(ref ownSlots) & (1 << slot)) != 0) return true;
        lock (OwnGate)
        {
            foreach (VirtualPad output in Outputs)
            {
                if (output.ownSlot >= 0) continue;
                try
                {
                    int assigned = output.pad.UserIndex;
                    if (assigned is >= 0 and < 4)
                    {
                        output.ownSlot = assigned;
                        Interlocked.Or(ref ownSlots, 1 << assigned);
                    }
                }
                catch (Xbox360UserIndexNotReportedException) { /* XInput assigns the index asynchronously. */ }
            }
        }
        return (Volatile.Read(ref ownSlots) & (1 << slot)) != 0;
    }
    private readonly ViGEmClient client = new();
    private readonly IXbox360Controller pad;

    internal VirtualPad()
    {
        try
        {
            pad = client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            lock (OwnGate) Outputs.Add(this);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal void Update(PadState state)
    {
        pad.ButtonState = state.Buttons;
        pad.LeftTrigger = state.LT;
        pad.RightTrigger = state.RT;
        pad.LeftThumbX = state.LX;
        pad.LeftThumbY = state.LY;
        pad.RightThumbX = state.RX;
        pad.RightThumbY = state.RY;
        pad.SubmitReport();
    }

    public void Dispose()
    {
        lock (OwnGate)
        {
            try
            {
                try { Update(default); }
                finally { pad.Disconnect(); }
            }
            finally
            {
                Outputs.Remove(this);
                if (ownSlot is >= 0 and < 4) Interlocked.And(ref ownSlots, ~(1 << ownSlot));
                client.Dispose();
            }
        }
    }
}
