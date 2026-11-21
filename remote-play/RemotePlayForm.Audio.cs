namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly ComboBox hostAudioMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox hostAudioApplication = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button refreshAudioApplications = new() { Text = "Actualizar aplicaciones", Width = 205 };
    private readonly Label audioSelectionHint = new() { AutoSize = true };
    private string? preferredAudioApplication;

    private void InitializeAudioSelection()
    {
        hostAudioMode.Items.AddRange(["Toda la salida de audio", "Solo una aplicación", "Todo excepto una aplicación"]);
        hostAudioMode.SelectedIndex = 0;
        RefreshAudioApplications(null);
        hostAudioMode.SelectedIndexChanged += (_, _) => RefreshAudioControls();
        hostAudioApplication.SelectedIndexChanged += (_, _) =>
        {
            if (hostAudioApplication.SelectedItem is AudioApplication application)
                preferredAudioApplication = application.Name;
            RefreshAudioControls();
        };
        hostAudio.CheckedChanged += (_, _) => RefreshAudioControls();
        refreshAudioApplications.Click += (_, _) => RefreshAudioApplications(
            (hostAudioApplication.SelectedItem as AudioApplication)?.Name ?? preferredAudioApplication);
        RefreshAudioControls();
    }

    private void RefreshAudioApplications(string? preferred)
    {
        hostAudioApplication.Items.Clear();
        hostAudioApplication.Items.Add("Selecciona el juego o la aplicación…");
        AudioApplication[] applications = AudioApplication.List();
        hostAudioApplication.Items.AddRange(applications);
        AudioApplication[] matches = applications.Where(item => string.Equals(item.Name, preferred,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        hostAudioApplication.SelectedIndex = matches.Length == 1 ? Array.IndexOf(applications, matches[0]) + 1 : 0;
    }

    private void RestoreAudioSelection(AppSettings settings)
    {
        hostAudioMode.SelectedIndex = settings.HostAudioMode is >= 0 and <= 2 ? settings.HostAudioMode : 1;
        preferredAudioApplication = settings.HostAudioApplication;
        RefreshAudioApplications(preferredAudioApplication);
        RefreshAudioControls();
    }

    private void RefreshAudioControls()
    {
        bool editable = hostAudio.Checked && !closing;
        hostAudioMode.Enabled = editable;
        hostAudioApplication.Enabled = refreshAudioApplications.Enabled = editable && hostAudioMode.SelectedIndex > 0;
        hostAudioDevice.Enabled = editable && hostAudioMode.SelectedIndex == 0;
        string application = (hostAudioApplication.SelectedItem as AudioApplication)?.Name ?? "la aplicación que selecciones";
        audioSelectionHint.Text = hostAudioMode.SelectedIndex switch
        {
            1 => $"Se transmite SOLO {application} y sus procesos. Las demás aplicaciones quedan fuera.",
            2 => $"Se transmite todo EXCEPTO {application} y sus procesos. Las demás aplicaciones sí se escuchan.",
            _ => "Se envía todo lo que suena en la salida elegida, incluidas las llamadas."
        };
    }

    private bool ValidateAudioSelection()
    {
        if (!hostAudio.Checked || hostAudioMode.SelectedIndex == 0) return true;
        if (!ProcessAudioCapture.Supported)
        {
            hostStatus.Text = "El filtro de audio requiere Windows 11 o una versión compatible de Windows.";
            return false;
        }
        if (hostAudioApplication.SelectedItem is not AudioApplication application)
        {
            hostStatus.Text = "Abre la aplicación de audio, pulsa «Actualizar aplicaciones» y selecciónala.";
            return false;
        }
        try { using var process = application.Open(); return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            hostStatus.Text = "La aplicación de audio ya no está disponible. Actualiza la lista y vuelve a elegirla.";
            return false;
        }
    }
}
