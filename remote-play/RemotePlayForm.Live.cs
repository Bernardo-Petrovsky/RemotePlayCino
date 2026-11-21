namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly Button applyHostChanges = new() { Text = "Aplicar a la sesión", Width = 205 };
    private readonly Label liveSettingsStatus = new() { AutoSize = true, Text = "Durante una sesión puedes editar estos ajustes y aplicarlos sin cambiar el código." };
    private LiveHostSettings? liveHostSettings;
    private HostMousePriority? hostMousePriority;
    private readonly Label mousePriorityStatus = new() { AutoSize = true, Text = "Tu mouse tiene prioridad sobre el de los invitados mientras lo usas." };

    private HostOptions ReadHostOptions() => new(hostAudio.Checked, hostInput.Checked,
        Qualities.BandwidthOptions[hostBandwidth.SelectedIndex], Qualities.FpsOptions[hostMaxFps.SelectedIndex],
        hostEncoder.SelectedIndex, Math.Max(0, hostDisplay.SelectedIndex),
        audioDeviceIds[Math.Max(0, hostAudioDevice.SelectedIndex)], (int)hostIdle.Value, hostLock.Checked,
        hostLocal1440.Checked, hostAudioMode.SelectedIndex, hostAudioApplication.SelectedItem as AudioApplication,
        hostApprove.Checked, MousePriority: hostMousePriority);

    private void InitializeLiveSettings()
    {
        foreach (ComboBox box in new[] { hostBandwidth, hostMaxFps, hostEncoder, hostDisplay, hostAudioDevice, hostAudioMode, hostAudioApplication })
            box.SelectedIndexChanged += (_, _) => MarkHostChanges();
        foreach (CheckBox box in new[] { hostAudio, hostInput, hostApprove, hostLock, hostLocal1440 })
            box.CheckedChanged += (_, _) => MarkHostChanges();
        hostIdle.ValueChanged += (_, _) => MarkHostChanges();
        applyHostChanges.Click += (_, _) => ApplyHostChanges();
        applyHostChanges.Enabled = false;
    }

    private void MarkHostChanges()
    {
        if (liveHostSettings is null || closing) return;
        applyHostChanges.Enabled = true;
        liveSettingsStatus.Text = "Cambios pendientes. Pulsa «Aplicar a la sesión». Audio o video pueden pausarse brevemente.";
    }

    private void ApplyHostChanges()
    {
        if (liveHostSettings is not { } live || closing || !ValidateAudioSelection()) return;
        HostOptions next = ReadHostOptions();
        HostOptions before = live.Current.Options;
        if (!LiveHostSettings.SameVideo(before, next) && guestCards.Keys.Any(guest => !guest.SupportsLiveVideo))
        {
            liveSettingsStatus.Text = "Un invitado usa una versión antigua. Debe actualizarse y reconectar para cambiar el video en vivo.";
            return;
        }
        live.Apply(next);
        applyHostChanges.Enabled = false;
        liveSettingsStatus.Text = "Cambios aplicados a la sesión. El código y los invitados se conservan. «Guardar cambios» los conserva para otra sesión.";
    }
}
