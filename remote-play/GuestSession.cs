using System.Text;

namespace RemotePlay;

[Flags]
internal enum GuestDevices { None = 0, Pad = 1, Keyboard = 2, Mouse = 4, Desktop = Keyboard | Mouse }

internal sealed class GuestSession(string name, string endpoint, GuestDevices defaults, bool requestedInput,
    bool managed, CancellationTokenSource cancellation, bool supportsLiveVideo = false, HostMousePriority? mousePriority = null)
{
    internal const int Limit = 4;
    internal const int NameBytes = 96;
    internal Lock Gate { get; } = new();
    internal string Name { get; } = name;
    internal string Endpoint { get; } = endpoint;
    internal bool SupportsLiveVideo { get; } = supportsLiveVideo;
    internal HostMousePriority? MousePriority { get; } = mousePriority;
    internal GuestDevices EffectiveDesktopPermissions => MousePriority?.Active == true
        ? Permissions & GuestDevices.Keyboard : Permissions & GuestDevices.Desktop;
    internal bool CanManageDesktop { get; } = requestedInput && (managed || (defaults & GuestDevices.Desktop) != GuestDevices.None);
    internal bool InputConnected { get; set; }
    internal GuestDevices Permissions { get; private set; } = defaults;
    internal Action<GuestDevices>? Revoked { get; set; }
    internal bool Closing { get; private set; }
    private Task kick = Task.CompletedTask;

    internal void SetPermission(GuestDevices device, bool allowed)
    {
        lock (Gate)
        {
            if (Closing)
                return;
            GuestDevices previous = Permissions;
            Permissions = allowed ? previous | device : previous & ~device;
            if ((previous & ~Permissions) != GuestDevices.None)
                Revoked?.Invoke(previous & ~Permissions);
        }
    }

    internal Task KickAsync()
    {
        lock (Gate)
        {
            if (Closing)
                return kick;
            Closing = true;
            GuestDevices previous = Permissions;
            Permissions = GuestDevices.None;
            Revoked?.Invoke(previous);
            kick = cancellation.CancelAsync();
            return kick;
        }
    }

    internal Task Finish()
    {
        lock (Gate)
        {
            Closing = true;
            return kick;
        }
    }

    internal static string CleanName(string? value) =>
        value is null ? "" : string.Concat(value.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format)).Trim();

    internal static byte[] EncodeName(string? value)
    {
        byte[] full = Encoding.UTF8.GetBytes(CleanName(value));
        if (full.Length <= NameBytes)
            return full;
        int length = NameBytes;
        while ((full[length] & 0xC0) == 0x80)
            length--;
        return full[..length];
    }
}
