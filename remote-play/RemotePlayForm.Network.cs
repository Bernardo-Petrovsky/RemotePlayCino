using System.Runtime.InteropServices;

namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly TextBox publicAddress = new() { ReadOnly = true, Text = "Detectando…" };
    private readonly Button copyPublicAddress = new() { Text = "Copiar IP para Internet", Width = 178, Enabled = false };
    private readonly Button refreshPublicAddress = new() { Text = "Actualizar IP", Width = 116 };
    private readonly System.Windows.Forms.Timer publicAddressTimer = new() { Interval = 300_000 };
    private readonly CancellationTokenSource publicAddressStop = new();
    private bool findingPublicAddress;
    private string? detectedPublicAddress;

    private void ConfigurePublicAddress()
    {
        Shown += async (_, _) => await RefreshPublicAddressAsync();
        publicAddressTimer.Tick += async (_, _) => await RefreshPublicAddressAsync();
        refreshPublicAddress.Click += async (_, _) => await RefreshPublicAddressAsync();
        copyPublicAddress.Click += (_, _) =>
        {
            if (detectedPublicAddress is not { } address)
                return;
            try
            {
                Clipboard.SetDataObject(address, copy: true, retryTimes: 5, retryDelay: 60);
                copyFeedback.Text = "IP para Internet copiada · comparte también el puerto y el código";
            }
            catch (ExternalException) { copyFeedback.Text = "Portapapeles ocupado. Reintenta."; }
            copyFeedback.Visible = true;
        };
        Disposed += DisposePublicAddress;
        publicAddressTimer.Start();
    }

    private void DisposePublicAddress(object? sender, EventArgs e)
    {
        Disposed -= DisposePublicAddress;
        publicAddressTimer.Dispose();
        publicAddressStop.Cancel();
        publicAddressStop.Dispose();
    }

    private async Task RefreshPublicAddressAsync()
    {
        if (findingPublicAddress || closing || IsDisposed)
            return;
        findingPublicAddress = true;
        refreshPublicAddress.Enabled = false;
        try
        {
            string address = (await PublicAddress.FindAsync(publicAddressStop.Token)).ToString();
            if (closing || IsDisposed)
                return;
            detectedPublicAddress = address;
            publicAddress.Text = address;
            copyPublicAddress.Enabled = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (closing || IsDisposed)
                return;
            detectedPublicAddress = null;
            publicAddress.Text = "Sin detectar · pulsa Actualizar IP";
            copyPublicAddress.Enabled = false;
        }
        finally
        {
            findingPublicAddress = false;
            if (!closing && !IsDisposed)
                refreshPublicAddress.Enabled = true;
        }
    }
}
