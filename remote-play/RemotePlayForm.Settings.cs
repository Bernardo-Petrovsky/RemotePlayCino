namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly Label settingsFeedback = new() { AutoSize = true };
    private readonly List<string> displayIds = [];
    private readonly string settingsPath;

    private void SaveSettings()
    {
        if (RemoteSession.IsReservedPort((int)hostPort.Value) || RemoteSession.IsReservedPort((int)clientPort.Value))
        {
            settingsFeedback.Text = RemoteSession.ReservedPortMessage;
            return;
        }
        var settings = new AppSettings
        {
            HostAddress = (hostAddress.SelectedItem as BindAddress)?.Address.ToString(),
            HostPort = (int)hostPort.Value,
            HostBandwidth = Qualities.BandwidthOptions[hostBandwidth.SelectedIndex],
            HostMaxFps = Qualities.FpsOptions[hostMaxFps.SelectedIndex],
            HostEncoder = hostEncoder.SelectedIndex,
            HostDisplay = hostDisplay.SelectedIndex >= 0 ? displayIds[hostDisplay.SelectedIndex] : null,
            HostAudioDevice = audioDeviceIds[hostAudioDevice.SelectedIndex],
            HostAudioMode = hostAudioMode.SelectedIndex,
            HostAudioApplication = (hostAudioApplication.SelectedItem as AudioApplication)?.Name ?? preferredAudioApplication,
            HostIdle = (int)hostIdle.Value,
            HostAudio = hostAudio.Checked,
            HostInput = hostInput.Checked,
            HostLock = hostLock.Checked,
            HostApprove = hostApprove.Checked,
            HostLimitLocal1440 = hostLocal1440.Checked,
            HostPriority = hostPriority.Checked,
            ClientAddress = clientAddress.Text.Trim(),
            ClientName = GuestSession.CleanName(clientName.Text),
            ClientPort = (int)clientPort.Value,
            ClientWidth = Qualities.Resolutions[clientResolution.SelectedIndex].Width,
            ClientFps = Qualities.FpsOptions[clientFps.SelectedIndex],
            ClientDecoder = clientDecoder.SelectedIndex,
            ClientPad = string.IsNullOrEmpty(controllerKey) ? 0 : 1,
            ClientControllerKey = controllerKey ?? "",
            ClientControllerName = controllerName,
            ClientNintendoLetters = nintendoLetters.Checked,
            ClientAudio = clientAudio.Checked,
            ClientInput = clientInput.Checked
        };

        try
        {
            settings.Save(settingsPath);
            settingsFeedback.Text = "Cambios guardados";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settingsFeedback.Text = "No se pudo guardar";
            MessageBox.Show(this, ex.Message, "Guardar cambios", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RestoreSettings(AppSettings settings)
    {
        Select(hostAddress, hostAddress.Items.Cast<BindAddress>().ToList()
            .FindIndex(item => item.Address.ToString() == settings.HostAddress));
        SetPort(hostPort, settings.HostPort);
        Select(hostBandwidth, Array.IndexOf(Qualities.BandwidthOptions, settings.HostBandwidth ?? -1));
        Select(hostMaxFps, Array.IndexOf(Qualities.FpsOptions, settings.HostMaxFps ?? -1));
        Select(hostEncoder, settings.HostEncoder);
        Select(hostDisplay, displayIds.FindIndex(id => id == settings.HostDisplay));
        Select(hostAudioDevice, audioDeviceIds.IndexOf(settings.HostAudioDevice));
        SetNumber(hostIdle, settings.HostIdle);
        hostAudio.Checked = settings.HostAudio;
        RestoreAudioSelection(settings);
        hostInput.Checked = settings.HostInput;
        hostLock.Checked = settings.HostLock;
        hostApprove.Checked = settings.HostApprove;
        hostLocal1440.Checked = settings.HostLimitLocal1440;
        hostPriority.Checked = settings.HostPriority;

        clientAddress.Text = settings.ClientAddress ?? "";
        clientName.Text = GuestSession.CleanName(settings.ClientName);
        SetPort(clientPort, settings.ClientPort);
        Select(clientResolution, Array.FindIndex(Qualities.Resolutions, item => item.Width == settings.ClientWidth));
        Select(clientFps, Array.IndexOf(Qualities.FpsOptions, settings.ClientFps ?? -1));
        Select(clientDecoder, settings.ClientDecoder);
        clientAudio.Checked = settings.ClientAudio;
        clientInput.Checked = settings.ClientInput;
    }

    private static void Select(ComboBox control, int? index)
    {
        if (index is int value && value >= 0 && value < control.Items.Count)
            control.SelectedIndex = value;
    }

    private static void SetNumber(NumericUpDown control, int? number)
    {
        if (number is int value && value >= control.Minimum && value <= control.Maximum)
            control.Value = value;
    }

    private static void SetPort(NumericUpDown control, int? port)
    {
        if (port is int value && !RemoteSession.IsReservedPort(value))
            SetNumber(control, value);
    }
}
