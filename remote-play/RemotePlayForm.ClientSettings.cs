namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly Button applyClientChanges = new() { Text = "Aplicar y reconectar", Width = 230 };
    private readonly Button returnToViewer = new() { Text = "Volver al visor", Width = 140, Enabled = false };
    private readonly Label clientSettingsStatus = new() { Text = "Puedes editar resolución, FPS, decodificador y audio durante la conexión." };
    private Action<bool>? selectSettingsPage;
    private sealed record ClientProfile(bool Audio, int Resolution, int Fps, int Decoder);
    private ClientProfile? activeClientProfile;
    private bool clientConnected;
    private Task clientApplyTask = Task.CompletedTask;

    private ClientProfile ReadClientProfile() => new(clientAudio.Checked, clientResolution.SelectedIndex,
        clientFps.SelectedIndex, clientDecoder.SelectedIndex);

    private void InitializeClientSettings()
    {
        foreach (ComboBox box in new[] { clientResolution, clientFps, clientDecoder })
            box.SelectedIndexChanged += (_, _) => MarkClientChanges();
        clientAudio.CheckedChanged += (_, _) => MarkClientChanges();
        applyClientChanges.Click += (_, _) =>
        {
            if (clientApplyTask.IsCompleted) clientApplyTask = ApplyClientChangesAsync();
        };
        returnToViewer.Click += (_, _) => ReturnToViewer();
        MarkClientChanges();
    }

    private void MarkClientChanges()
    {
        bool pending = clientStop is not null && ReadClientProfile() != activeClientProfile;
        applyClientChanges.Enabled = clientConnected && pending && clientApplyTask.IsCompleted && !closing;
        if (pending)
            clientSettingsStatus.Text = "Cambios pendientes. «Aplicar y reconectar» pausa la imagen y puede pedir aprobación de nuevo.";
        else if (clientStop is not null)
            clientSettingsStatus.Text = "Puedes editar estos ajustes. Aplicarlos reconecta brevemente. Mouse, teclado y mando se cambian en vivo.";
        else
            clientSettingsStatus.Text = "Edita los ajustes antes de conectar. «Guardar cambios» los conserva para otra sesión.";
        returnToViewer.Enabled = viewer is not null;
    }

    private async Task ApplyClientChangesAsync()
    {
        if (!clientConnected || clientStop is not { } stop || closing) return;
        applyClientChanges.Enabled = false;
        clientButton.Enabled = false;
        Task previous = clientTask;
        try
        {
            await StopClientAsync(stop);
            await previous;
            if (!closing) clientTask = StartClientAsync();
        }
        finally
        {
            clientButton.Enabled = !closing;
            Post(MarkClientChanges);
        }
    }

    private void SetClientConnectionFields(bool readOnly)
    {
        clientAddress.ReadOnly = clientCode.ReadOnly = clientName.ReadOnly = readOnly;
        clientPort.ReadOnly = readOnly;
        clientPort.Increment = readOnly ? 0 : 1;
    }

    private void ShowClientSettings()
    {
        selectSettingsPage?.Invoke(false);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        clientResolution.Focus();
    }

    private void ReturnToViewer()
    {
        if (viewer is null) return;
        if (viewer.WindowState == FormWindowState.Minimized) viewer.WindowState = FormWindowState.Normal;
        viewer.Show();
        viewer.Activate();
        video.Focus();
    }
}
