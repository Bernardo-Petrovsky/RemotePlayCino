namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly Label updateStatus = new() { AutoSize = true };
    private readonly Button checkUpdate = new() { Text = "Buscar actualización", Width = 210 };
    private readonly Button installUpdate = new() { Text = "Instalar y reiniciar", Width = 250, Visible = false };
    private Updater.Release? availableUpdate;
    private bool updating;

    private Control BuildUpdateCard()
    {
        TableLayoutPanel card = HostCard();
        card.Controls.Add(Heading("Actualizaciones"));
        card.Controls.Add(Note($"Versión instalada: {Updater.Current}. Las versiones nuevas se descargan de GitHub ({Updater.Repository}).", card));
        StyleButton(checkUpdate);
        StyleButton(installUpdate, primary: true);
        checkUpdate.Margin = new Padding(0, 0, 10, 0);
        installUpdate.Margin = Padding.Empty;
        checkUpdate.Click += async (_, _) => await CheckForUpdateAsync(manual: true);
        installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        card.Controls.Add(StatusLine(updateStatus, card, TextColor));
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Margin = new Padding(0, 12, 0, 0), BackColor = Surface
        };
        row.Controls.AddRange(checkUpdate, installUpdate);
        card.Controls.Add(row);
        return card;
    }

    // Looks once, quietly, when the window first appears; failures only speak when the person asks.
    private void StartUpdateCheck() => Shown += async (_, _) => await CheckForUpdateAsync(manual: false);

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (updating || !checkUpdate.Enabled)
            return;
        checkUpdate.Enabled = false;
        if (manual)
            updateStatus.Text = "Buscando…";
        try
        {
            availableUpdate = await Updater.CheckAsync();
            if (closing)
                return;
            installUpdate.Visible = availableUpdate is not null;
            if (availableUpdate is { } found)
            {
                installUpdate.Text = $"Instalar {found.Version} y reiniciar";
                updateStatus.Text = $"Hay una versión nueva: {found.Version}. Desconecta antes de instalarla.";
            }
            else
                updateStatus.Text = "Tienes la última versión.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException or UriFormatException)
        {
            Trace.Note($"Búsqueda de actualización: {ex.Message}");
            if (manual && !closing)
                updateStatus.Text = ex is InvalidOperationException ? ex.Message : "No se pudo consultar GitHub. Revisa tu conexión.";
        }
        finally
        {
            if (!closing)
                checkUpdate.Enabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (availableUpdate is not { } release || updating)
            return;
        if (clientStop is not null || hostStop is not null)
        {
            updateStatus.Text = "Desconecta el cliente y detén el anfitrión antes de actualizar.";
            return;
        }
        updating = true;
        checkUpdate.Enabled = installUpdate.Enabled = false;
        try
        {
            string script = await Task.Run(() => Updater.PrepareAsync(release, text => Post(() => updateStatus.Text = text)));
            updateStatus.Text = "Reiniciando con la versión nueva…";
            Updater.Launch(script);
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Trace.Note($"Actualización fallida: {ex.Message}");
            updateStatus.Text = "No se pudo actualizar: " + ex.Message;
            updating = false;
            checkUpdate.Enabled = installUpdate.Enabled = true;
        }
    }
}
