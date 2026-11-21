namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private sealed record ControllerChoice(string Key, string Name, ControllerDevice? Device)
    { public override string ToString() => Name; }
    private readonly CheckBox nintendoLetters = new() { Text = "Usar letras Nintendo", AutoSize = true };
    private readonly Label controllerHint = new() { AutoSize = true };
    private ControllerInput? controllers;
    private string? controllerKey;
    private string controllerName = "Mando guardado";
    private long controllerRevision = -1;
    private int legacyPadSlot;
    private bool updatingControllers;

    private void InitializeControllers(AppSettings? settings)
    {
        controllerKey = settings?.ClientControllerKey;
        if (controllerKey is null && settings?.ClientPad == 0) controllerKey = "";
        controllerName = GuestSession.CleanName(settings?.ClientControllerName);
        if (controllerName.Length == 0) controllerName = "Mando guardado";
        legacyPadSlot = Math.Clamp((settings?.ClientPad ?? 1) - 1, 0, 3);
        nintendoLetters.Checked = settings?.ClientNintendoLetters ?? false;
        controllers = new ControllerInput();
        controllers.SetNintendoLetters(nintendoLetters.Checked);
        clientPad.SelectedIndexChanged += (_, _) =>
        {
            if (updatingControllers || clientPad.SelectedItem is not ControllerChoice choice) return;
            controllerKey = choice.Key;
            controllerName = choice.Device?.Name ?? choice.Name;
            controllers.Select(choice.Device);
            nintendoLetters.Enabled = choice.Device?.Nintendo == true;
            RefreshControllerHint();
        };
        nintendoLetters.CheckedChanged += (_, _) =>
        { controllers.SetNintendoLetters(nintendoLetters.Checked); RefreshControllerHint(); };
        RefreshControllerList();
    }

    private void RefreshControllerList()
    {
        if (controllers is null || closing || clientPad.DroppedDown) return;
        ControllerInput.Snapshot current = controllers.Current;
        if (controllerRevision == current.Revision) return;
        controllerRevision = current.Revision;
        if (controllerKey is null && current.Devices.Length != 0)
        {
            ControllerDevice chosen = current.Devices.FirstOrDefault(device => device.Nintendo || device.Name.Contains("DualShock", StringComparison.Ordinal))
                ?? current.Devices.FirstOrDefault(device => device.PlayerIndex == legacyPadSlot) ?? current.Devices[0];
            controllerKey = chosen.Key;
            controllerName = chosen.Name;
        }
        var choices = new List<ControllerChoice> { new("", "Sin mando", null) };
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ControllerDevice device in current.Devices)
        {
            int index = names.GetValueOrDefault(device.Name) + 1;
            names[device.Name] = index;
            bool duplicate = current.Devices.Count(other => other.Name == device.Name) > 1;
            choices.Add(new(device.Key, duplicate ? $"{device.Name} · {index}" : device.Name, device));
        }
        if (!string.IsNullOrEmpty(controllerKey) && choices.All(choice => choice.Key != controllerKey))
            choices.Add(new(controllerKey, controllerName + " · desconectado", null));
        ControllerChoice selected = choices.FirstOrDefault(choice => choice.Key == controllerKey) ?? choices[0];
        updatingControllers = true;
        clientPad.BeginUpdate();
        try
        {
            clientPad.Items.Clear();
            clientPad.Items.AddRange(choices.ToArray());
            clientPad.SelectedItem = selected;
        }
        finally { clientPad.EndUpdate(); updatingControllers = false; }
        controllers.Select(selected.Device);
        nintendoLetters.Enabled = selected.Device?.Nintendo == true;
        RefreshControllerHint();
    }

    private void RefreshControllerHint()
    {
        if (controllers?.Current.Error is { } error) { controllerHint.Text = error; return; }
        if (clientPad.SelectedItem is ControllerChoice { Device.Nintendo: true })
            controllerHint.Text = nintendoLetters.Checked
                ? "Letras Nintendo: A/B y X/Y conservan su letra en el mando del juego."
                : "Por posición: B Nintendo → A Xbox; A → B; Y → X; X → Y.";
        else
            controllerHint.Text = "Detección directa por USB o Bluetooth. El anfitrión recibe un mando Xbox virtual.";
    }

    private void RefreshControllerReadout()
    {
        RefreshControllerList();
        if (clientPad.SelectedItem is not ControllerChoice { Device: { } device })
        {
            if (controllerKey == "") padReadout.Text = "Envío del mando desactivado.";
            else if (controllerKey is null) padReadout.Text = "Conecta un mando para detectarlo directamente.";
            else padReadout.Text = "Mando desconectado. La entrada remota queda liberada hasta que vuelva el mismo dispositivo.";
            return;
        }
        if (controllers?.Poll() is not { } pad)
        {
            padReadout.Text = $"{device.Name}\nEsperando datos del mando. Si otra aplicación lo usa en exclusiva, ciérrala.";
            return;
        }
        string buttons = pad.Buttons == 0 ? "—" : ((PadButtons)pad.Buttons).ToString();
        padReadout.Text = $"{device.Name}\nAl juego  {buttons}\nIzq.  {pad.LX}, {pad.LY}\nDer.  {pad.RX}, {pad.RY}\nLT  {pad.LT}    RT  {pad.RT}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            viewer?.Dispose();
            video.Dispose();
            controllers?.Dispose();
        }
        base.Dispose(disposing);
    }
}
