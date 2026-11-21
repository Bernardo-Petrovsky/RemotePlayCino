using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    // Colors come from the active theme so a style change reaches custom painting without rebuilding the window.
    private static Color Background => Theme.Current.Background;
    private static Color Surface => Theme.Current.Surface;
    private static Color Input => Theme.Current.Input;
    private static Color TextColor => Theme.Current.Text;
    private static Color Muted => Theme.Current.Muted;
    private static Color Accent => Theme.Current.Accent;
    private static Color AccentHover => Theme.Current.AccentHover;
    private static Color Outline => Theme.Current.Outline;
    private static Color Danger => Theme.Current.Danger;
    private static Color SelectedTint => Theme.Current.Selected;

    private readonly Button fullscreenButton = new() { Text = "Pantalla completa · F11", Enabled = false };

    private void BuildLayout()
    {
        BackColor = Background;
        ForeColor = TextColor;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
            Padding = new Padding(24, 0, 22, 0), BackColor = Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var identity = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty
        };
        var logo = new PictureBox
        {
            Image = AppBrand.CreateLogo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(42, 42),
            Margin = new Padding(0, 0, 10, 0), AccessibleName = "Logo de " + AppBrand.ShortName + ": mando y café"
        };
        logo.Disposed += (_, _) => logo.Image?.Dispose();
        var brand = new Label
        {
            Text = AppBrand.ShortName, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 0),
            Font = new Font(Font.FontFamily, 17, FontStyle.Bold), ForeColor = TextColor
        };
        identity.Controls.AddRange(logo, brand);
        var nav = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var hostTab = new Button { Text = "Anfitrión", Width = 105, AccessibleName = "Vista Anfitrión" };
        var clientTab = new Button { Text = "Cliente", Width = 105, AccessibleName = "Vista Cliente" };
        var appearanceTab = new Button { Text = "Ajustes", Width = 105, AccessibleName = "Vista Ajustes: apariencia y actualizaciones" };
        var save = new Button { Text = "Guardar cambios", Width = 142 };
        StyleButton(save);
        save.Click += (_, _) => SaveSettings();
        settingsFeedback.ForeColor = Muted;
        settingsFeedback.Margin = new Padding(4, 12, 8, 0);
        nav.Controls.AddRange(settingsFeedback, save, hostTab, clientTab, appearanceTab);
        header.Controls.Add(identity, 0, 0);
        header.Controls.Add(nav, 1, 0);
        root.Controls.Add(header, 0, 0);

        var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(22, 20, 22, 22) };
        var host = BuildHostPage();
        var client = BuildClientPage();
        var appearance = BuildAppearancePage();
        content.Controls.AddRange(host, client, appearance);
        root.Controls.Add(content, 0, 2);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
            Padding = new Padding(24, 8, 22, 8), BackColor = Surface
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var applySlot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 9, 10, 9) };
        StyleButton(applyHostChanges, primary: true);
        StyleButton(applyClientChanges, primary: true);
        applyHostChanges.Dock = applyClientChanges.Dock = DockStyle.Fill;
        applySlot.Controls.AddRange(applyHostChanges, applyClientChanges);
        var statusSlot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (Label label in new[] { liveSettingsStatus, clientSettingsStatus })
        {
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextColor;
            label.TextAlign = ContentAlignment.MiddleLeft;
            statusSlot.Controls.Add(label);
        }
        StyleButton(returnToViewer);
        returnToViewer.Anchor = AnchorStyles.Right;
        actions.Controls.Add(applySlot, 0, 0);
        actions.Controls.Add(statusSlot, 1, 0);
        actions.Controls.Add(returnToViewer, 2, 0);
        root.Controls.Add(actions, 0, 1);

        void ShowPage(int page)
        {
            bool showHost = page == 0, showClient = page == 1;
            host.Visible = showHost;
            client.Visible = showClient;
            appearance.Visible = page == 2;
            applyHostChanges.Visible = liveSettingsStatus.Visible = showHost;
            applyClientChanges.Visible = clientSettingsStatus.Visible = returnToViewer.Visible = showClient;
            root.RowStyles[1].Height = page == 2 ? 0 : 76;
            StyleButton(hostTab, showHost);
            StyleButton(clientTab, showClient);
            StyleButton(appearanceTab, page == 2);
        }
        void SelectPage(bool showHost) => ShowPage(showHost ? 0 : 1);
        hostTab.Click += (_, _) => ShowPage(0);
        clientTab.Click += (_, _) => ShowPage(1);
        appearanceTab.Click += (_, _) => ShowPage(2);
        selectSettingsPage = SelectPage;
        SelectPage(true);
    }

    private Control BuildHostPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoScroll = true, Padding = new Padding(24, 14, 24, 16)
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));

        var column = Stack(Background, Padding.Empty);
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.Dock = DockStyle.Top;
        column.Margin = new Padding(0, 0, 16, 0);

        // The first card holds everything a session needs; the rest is tuning, grouped by purpose.
        var card = HostCard();
        card.Paint += (_, e) =>
        {
            using var edge = new SolidBrush(Accent);
            e.Graphics.FillRectangle(edge, 0, 0, 4, card.Height);
        };
        card.Controls.Add(Heading("Compartir pantalla"));
        card.Controls.Add(FieldRow(Field("IP local del anfitrión", hostAddress), Field("Puerto", hostPort), 100));
        var copyIp = new Button { Text = "Copiar IP local", Width = 124 };
        StyleButton(copyIp);
        copyIp.Click += (_, _) => CopyHostAddress();
        card.Controls.Add(copyIp);
        card.Controls.Add(Field("IP para invitados por Internet", publicAddress));
        StyleButton(copyPublicAddress);
        StyleButton(refreshPublicAddress);
        var internetActions = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 5)
        };
        internetActions.Controls.AddRange(copyPublicAddress, refreshPublicAddress);
        card.Controls.Add(internetActions);
        card.Controls.Add(Note("Para otra red, comparte la IP para Internet, el puerto y el código de esta sesión.", card));
        StyleButton(copy);
        StyleButton(revealCode);
        copy.Height = revealCode.Height = 26;
        copy.Margin = new Padding(0, 0, 6, 0);
        revealCode.Margin = Padding.Empty;
        var codeActions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty
        };
        codeActions.Controls.AddRange(copy, revealCode);
        StyleInput(hostCode, "Código de acceso");
        card.Controls.Add(Field("Código de acceso", FieldRow(hostCode, codeActions, 156)));
        card.Controls.Add(StatusLine(copyFeedback, card, Muted));
        StyleButton(hostButton, primary: true);
        hostButton.Dock = DockStyle.Top;
        hostButton.Height = 42;
        hostButton.Margin = new Padding(0, 10, 0, 10);
        card.Controls.Add(hostButton);
        card.Controls.Add(StatusLine(hostStatus, card, TextColor));
        card.Controls.Add(StatusLine(hostPad, card, Muted));
        card.Controls.Add(StatusLine(hostVideo, card, Muted));
        card.Controls.Add(StatusLine(hostExtras, card, Muted));
        column.Controls.Add(card);

        var stream = HostCard();
        stream.Controls.Add(Heading("Imagen y sonido"));
        stream.Controls.Add(Subheading("Video"));
        stream.Controls.Add(FieldRow(Field("Video por invitado (Mbps)", hostBandwidth), Field("FPS máximos", hostMaxFps), 110));
        stream.Controls.Add(Note("La subida necesaria se suma por cada invitado conectado.", stream));
        stream.Controls.Add(Field("Pantalla a compartir", hostDisplay));
        stream.Controls.Add(Option(hostLocal1440));
        hostLocal1440.Checked = true;
        stream.Controls.Add(ClientDivider());
        stream.Controls.Add(Subheading("Audio"));
        stream.Controls.Add(Option(hostAudio));
        stream.Controls.Add(Field("Audio que recibe el invitado", hostAudioMode));
        stream.Controls.Add(Field("Aplicación de audio", hostAudioApplication));
        StyleButton(refreshAudioApplications);
        stream.Controls.Add(refreshAudioApplications);
        stream.Controls.Add(StatusLine(audioSelectionHint, stream, Muted));
        stream.Controls.Add(Note("El invitado debe activar «Escuchar el audio del anfitrión» antes de conectar.", stream));
        column.Controls.Add(stream);

        var guests = HostCard();
        guests.Controls.Add(Heading("Invitados y seguridad"));
        guests.Controls.Add(Option(hostApprove));
        hostApprove.Checked = true;
        guests.Controls.Add(Option(hostInput));
        guests.Controls.Add(StatusLine(mousePriorityStatus, guests, TextColor));
        guests.Controls.Add(Field("Desconectar invitado inactivo (minutos, 0 = nunca)", hostIdle));
        guests.Controls.Add(Option(hostLock));
        guests.Controls.Add(Note("Con la IP 127.0.0.1, bloquear el equipo cerraría tu propia sesión.", guests));
        column.Controls.Add(guests);

        var tuning = HostCard();
        tuning.Controls.Add(Heading("Opciones avanzadas"));
        var details = Stack(Surface, Padding.Empty);
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.Dock = DockStyle.Top;
        details.Margin = new Padding(0, 16, 0, 0);
        details.Visible = false;
        var toggle = new Button { Text = "Mostrar opciones avanzadas", AccessibleName = "Mostrar u ocultar las opciones avanzadas" };
        StyleButton(toggle);
        toggle.Dock = DockStyle.Top;
        toggle.Margin = Padding.Empty;
        toggle.Click += (_, _) =>
        {
            details.Visible = !details.Visible;
            toggle.Text = details.Visible ? "Ocultar opciones avanzadas" : "Mostrar opciones avanzadas";
        };
        details.Controls.Add(Field("Codificador", hostEncoder));
        details.Controls.Add(Field("Salida de audio (modo completo)", hostAudioDevice));
        details.Controls.Add(ClientDivider());
        details.Controls.Add(Subheading("Prioridad del equipo"));
        details.Controls.Add(Option(hostPriority));
        details.Controls.Add(StatusLine(localPriorityStatus, details, Muted));
        StyleButton(networkPriorityButton);
        details.Controls.Add(networkPriorityButton);
        details.Controls.Add(StatusLine(networkPriorityStatus, details, Muted));
        details.Controls.Add(Note("La prioridad de red se conserva entre sesiones y solo actúa al transmitir.", details));
        tuning.Controls.Add(toggle);
        tuning.Controls.Add(details);
        column.Controls.Add(tuning);

        page.Controls.Add(column, 0, 0);
        page.Controls.Add(BuildGuestPanel(), 1, 0);
        return page;
    }

    private static TableLayoutPanel HostCard()
    {
        var card = Stack(Surface, new Padding(24));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Dock = DockStyle.Top;
        card.Margin = new Padding(0, 0, 0, 16);
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Outline);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        return card;
    }

    private Label Subheading(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 10), ForeColor = Accent,
        Font = new Font(Font.FontFamily, 11, FontStyle.Bold)
    };

    private static Label Note(string text, Control parent)
    {
        Label note = StatusLine(new Label { AutoSize = true, Text = text }, parent, Muted);
        note.Margin = new Padding(0, 5, 0, 12);
        return note;
    }

    private Control BuildClientPage()
    {
        // Settings have no video child: the surface belongs only to the independent viewer.
        video.Presented += () => framesPresented++;
        video.Margin = Padding.Empty;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 12)
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        var connection = Stack(Surface, new Padding(22));
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connection.Dock = DockStyle.Top;
        connection.Margin = new Padding(0, 0, 16, 0);
        connection.Controls.Add(Heading("Conexión"));
        connection.Controls.Add(FieldRow(Field("Dirección IP", clientAddress), Field("Puerto", clientPort), 100));
        connection.Controls.Add(Field("Nombre de invitado", clientName));
        connection.Controls.Add(Field("Código de acceso", clientCode));
        StyleButton(clientButton, primary: true);
        clientButton.Dock = DockStyle.Top;
        clientButton.Margin = new Padding(0, 2, 0, 6);
        connection.Controls.Add(clientButton);
        connection.Controls.Add(StatusLine(clientStatus, connection, TextColor));
        connection.Controls.Add(ClientDivider());
        connection.Controls.Add(Heading("Visor de la sesión"));
        connection.Controls.Add(StatusLine(clientVideo, connection, TextColor));
        connection.Controls.Add(StatusLine(new Label
        {
            AutoSize = true,
            Text = "Al conectar, la imagen se abre en una ventana independiente. Puedes cambiar a otras aplicaciones con normalidad."
        }, connection, Muted));
        StyleButton(fullscreenButton);
        fullscreenButton.Dock = DockStyle.Top;
        fullscreenButton.Margin = new Padding(0, 16, 0, 0);
        fullscreenButton.Click += (_, _) =>
        {
            ReturnToViewer();
            viewer?.ToggleFullscreen();
        };
        connection.Controls.Add(fullscreenButton);
        page.Controls.Add(connection, 0, 0);

        var preferences = Stack(Background, Padding.Empty);
        preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preferences.Dock = DockStyle.Top;
        var image = Stack(Surface, new Padding(22));
        image.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        image.Dock = DockStyle.Top;
        image.Margin = new Padding(0, 0, 0, 16);
        image.Controls.Add(Heading("Imagen y audio"));
        image.Controls.Add(FieldRow(Field("Resolución", clientResolution), Field("FPS", clientFps), 140));
        image.Controls.Add(Field("Decodificador", clientDecoder));
        image.Controls.Add(Option(clientAudio));
        image.Controls.Add(StatusLine(new Label
        {
            AutoSize = true, Text = "Los FPS efectivos dependen de la captura, la red y la decodificación."
        }, image, Muted));
        preferences.Controls.Add(image);

        var devices = Stack(Surface, new Padding(22));
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        devices.Dock = DockStyle.Top;
        devices.Controls.Add(Heading("Mando, mouse y teclado"));
        devices.Controls.Add(Field("Mando local", clientPad));
        devices.Controls.Add(Option(nintendoLetters));
        devices.Controls.Add(StatusLine(controllerHint, devices, Muted));
        devices.Controls.Add(StatusLine(padReadout, devices, TextColor));
        devices.Controls.Add(ClientDivider());
        devices.Controls.Add(Option(clientInput));
        devices.Controls.Add(StatusLine(clientExtras, devices, Muted));
        devices.Controls.Add(StatusLine(new Label
        {
            AutoSize = true,
            Text = "Haz clic en la imagen del visor para enviar teclado. Con 127.0.0.1, el audio y el mouse actúan sobre este mismo equipo."
        }, devices, Muted));
        preferences.Controls.Add(devices);
        page.Controls.Add(preferences, 1, 0);
        scroll.Controls.Add(page);
        return scroll;
    }

    private static Panel ClientDivider() => new()
    {
        Height = 1, BackColor = Input, Dock = DockStyle.Top, Margin = new Padding(0, 18, 0, 18)
    };

    private ViewerForm? viewer;

    // The video opens in its own window so it can go fullscreen without any of the controls around it.
    private void OpenViewer()
    {
        if (viewer is not null)
            return;
        var window = new ViewerForm(video);
        viewer = window;
        returnToViewer.Enabled = fullscreenButton.Enabled = true;
        window.SettingsRequested += ShowClientSettings;
        window.FormClosed += async (_, _) =>
        {
            viewer = null;
            returnToViewer.Enabled = fullscreenButton.Enabled = false;
            if (!closing && clientStop is { } stop)
                await StopClientAsync(stop);
        };
        window.Show();
        video.Focus();
    }

    private void CloseViewer() => viewer?.Close();

    private Form CreateApprovalDialog(string name, string endpoint)
    {
        var dialog = new Form
        {
            Text = AppBrand.Name + " · Solicitud de acceso", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, AutoScaleMode = AutoScaleMode.Dpi,
            Font = Font, ClientSize = new Size(440, 174),
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Background, ForeColor = TextColor
        };
        AppBrand.ApplyIcon(dialog);
        var message = new Label
        {
            Text = $"{name} ({endpoint}) quiere entrar a tu sesión.\nSolo admítelo si lo conoces.",
            Dock = DockStyle.Top, Height = 100, Padding = new Padding(16, 16, 16, 0),
            ForeColor = TextColor, UseMnemonic = false
        };
        var allow = new Button { Text = "Admitir", DialogResult = DialogResult.Yes, Left = 92, Top = 122, Width = 110 };
        var deny = new Button { Text = "Denegar", DialogResult = DialogResult.No, Left = 222, Top = 122, Width = 110 };
        StyleButton(allow, primary: true);
        StyleButton(deny);
        deny.FlatAppearance.BorderSize = 1;
        deny.FlatAppearance.BorderColor = Muted;
        allow.Click += (_, _) => dialog.Close();
        deny.Click += (_, _) => dialog.Close();
        dialog.Controls.AddRange(message, allow, deny);
        dialog.CancelButton = deny;
        return dialog;
    }

    private Form ShowClosingNotice()
    {
        Form owner = viewer ?? (Form)this;
        var notice = new Form
        {
            Text = "Cerrando " + AppBrand.ShortName,
            StartPosition = FormStartPosition.Manual,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ControlBox = false,
            ShowInTaskbar = false,
            UseWaitCursor = true,
            ClientSize = new Size(360, 92),
            BackColor = Surface,
            ForeColor = TextColor
        };
        AppBrand.ApplyIcon(notice);
        notice.Controls.Add(new Label
        {
            Text = "Cerrando la sesión y liberando los recursos…",
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(12),
            Dock = DockStyle.Fill
        });
        Rectangle bounds = owner.Bounds;
        notice.Location = new Point(bounds.Left + (bounds.Width - notice.Width) / 2,
            bounds.Top + (bounds.Height - notice.Height) / 2);
        notice.Show(this);
        notice.Refresh();
        return notice;
    }

    private sealed class ViewerForm : Form
    {
        private readonly VideoSurface surface;
        private readonly ToolTip deviceTips = new();
        private readonly Label readout = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextColor };
        private readonly DeviceIndicator mouse = new("Mouse", DeviceKind.Mouse);
        private readonly DeviceIndicator keyboard = new("Teclado", DeviceKind.Keyboard);
        private readonly DeviceIndicator pad = new("Mando", DeviceKind.Pad);
        private bool fullscreen;
        private Rectangle restoreBounds;
        private FormWindowState restoreState;
        internal event Action? SettingsRequested;

        internal ViewerForm(VideoSurface surface)
        {
            this.surface = surface;
            Text = AppBrand.Name;
            AppBrand.ApplyIcon(this);
            BackColor = Color.Black;
            Font = new Font("Segoe UI", 11);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1280, 754);
            MinimumSize = new Size(480, 270);
            surface.Dock = DockStyle.Fill;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.Controls.Add(surface, 0, 0);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty,
                Padding = new Padding(10, 0, 8, 0), BackColor = Surface, ForeColor = TextColor
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var devices = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            devices.Controls.AddRange(mouse, keyboard, pad);
            footer.Controls.Add(readout, 0, 0);
            footer.Controls.Add(devices, 1, 0);
            var settings = new Button { Text = "Ajustes", Dock = DockStyle.Fill, AccessibleName = "Ajustes de la conexión" };
            StyleButton(settings);
            settings.Click += (_, _) => SettingsRequested?.Invoke();
            footer.Controls.Add(settings, 2, 0);
            layout.Controls.Add(footer, 0, 1);
            Controls.Add(layout);
            SetDeviceState(mouse, false);
            SetDeviceState(keyboard, false);
            SetDeviceState(pad, false);
        }

        internal void SetStatus(string text, GuestDevices devices, bool? padActive)
        {
            readout.Text = text;
            Text = AppBrand.Name + " · " + text.Replace("  ·  ", " · ");
            SetDeviceState(mouse, devices.HasFlag(GuestDevices.Mouse));
            SetDeviceState(keyboard, devices.HasFlag(GuestDevices.Keyboard));
            SetDeviceState(pad, padActive);
        }

        private void SetDeviceState(DeviceIndicator indicator, bool? active)
        {
            string status = indicator.SetState(active);
            if (deviceTips.GetToolTip(indicator) != status)
                deviceTips.SetToolTip(indicator, status);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The connection owns the surface. A reconnect must not inherit a disposed control.
                surface.Parent?.Controls.Remove(surface);
                deviceTips.Dispose();
            }
            base.Dispose(disposing);
        }

        internal void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (fullscreen)
            {
                restoreState = WindowState;
                restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = restoreBounds;
                WindowState = restoreState;
            }
            surface.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            if (keyData == Keys.Escape && fullscreen && (surface.DesktopDevices & GuestDevices.Keyboard) == GuestDevices.None)
            {
                ToggleFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    private enum DeviceKind { Mouse, Keyboard, Pad }

    private sealed class DeviceIndicator : Control
    {
        private readonly string label;
        private readonly DeviceKind kind;
        private bool? active;

        internal DeviceIndicator(string label, DeviceKind kind)
        {
            this.label = label;
            this.kind = kind;
            Size = new Size(29, 30);
            Margin = new Padding(1, 2, 1, 0);
            AccessibleRole = AccessibleRole.Graphic;
            DoubleBuffered = true;
        }

        internal string SetState(bool? state)
        {
            string status = label + (state switch { true => " activo", false => " inactivo", _ => " sin confirmación" });
            if (AccessibleName != status)
            {
                active = state;
                AccessibleName = status;
                Invalidate();
            }
            return status;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform((Width - 20) / 2f, (Height - 20) / 2f);
            using var pen = new Pen(active == true ? Accent : Muted, 1.35f) { LineJoin = LineJoin.Round };
            switch (kind)
            {
                case DeviceKind.Mouse:
                    g.DrawEllipse(pen, 5, 2, 10, 16);
                    g.DrawLine(pen, 10, 4, 10, 8);
                    break;
                case DeviceKind.Keyboard:
                    g.DrawRectangle(pen, 2, 5, 16, 10);
                    g.DrawLine(pen, 5, 8, 15, 8);
                    g.DrawLine(pen, 7, 12, 13, 12);
                    break;
                case DeviceKind.Pad:
                    g.DrawPolygon(pen, new Point(5, 6), new Point(15, 6), new Point(18, 11),
                        new Point(18, 15), new Point(15, 15), new Point(12, 12), new Point(8, 12),
                        new Point(5, 15), new Point(2, 15), new Point(2, 11));
                    g.DrawLine(pen, 5, 9, 9, 9);
                    g.DrawLine(pen, 7, 7, 7, 11);
                    g.DrawEllipse(pen, 13, 8, 2, 2);
                    break;
            }
            if (active == false)
                g.DrawLine(pen, 3, 17, 17, 3);
        }
    }

    private static CheckBox Option(CheckBox box)
    {
        box.AutoSize = true;
        box.Checked = false;
        box.ForeColor = TextColor;
        box.Margin = new Padding(0, 0, 0, 9);
        box.AccessibleName = box.Text;
        box.FlatStyle = FlatStyle.Flat;
        box.Paint += (_, e) => DrawOption(box, e);
        return box;
    }

    private static void DrawOption(CheckBox box, PaintEventArgs e)
    {
        e.Graphics.Clear(box.BackColor);
        System.Windows.Forms.VisualStyles.CheckBoxState state = box.Checked
            ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
            : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
        if (!box.Enabled)
            state = box.Checked ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled
                : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
        Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(0, Math.Max(0, (box.Height - glyph.Height) / 2)), state);
        Rectangle text = new(glyph.Width + 6, 0, Math.Max(0, box.Width - glyph.Width - 6), box.Height);
        TextRenderer.DrawText(e.Graphics, box.Text, box.Font, text, box.Enabled ? TextColor : Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (box.Focused && box.Enabled) ControlPaint.DrawFocusRectangle(e.Graphics, text, TextColor, box.BackColor);
    }

    private static TableLayoutPanel Stack(Color color, Padding padding) => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = color,
        Margin = Padding.Empty, Padding = padding
    };

    private Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 16), ForeColor = TextColor,
        Font = new Font(Font.FontFamily, 15, FontStyle.Bold)
    };

    private static Label StatusLine(Label label, Control parent, Color color)
    {
        label.ForeColor = color;
        label.Margin = new Padding(0, 5, 0, 0);
        parent.SizeChanged += (_, _) => label.MaximumSize =
            new Size(Math.Max(100, parent.ClientSize.Width - parent.Padding.Horizontal), 0);
        return label;
    }

    private static Control Field(string caption, Control input)
    {
        var field = Stack(Surface, Padding.Empty);
        field.Dock = DockStyle.Top;
        field.Margin = new Padding(0, 0, 0, 11);
        field.Controls.Add(new Label
        {
            Text = caption, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 5)
        });
        StyleInput(input, caption);
        field.Controls.Add(input);
        return field;
    }

    private static void StyleInput(Control input, string caption)
    {
        input.AccessibleName = caption;
        input.Dock = DockStyle.Top;
        input.Margin = Padding.Empty;
        switch (input)
        {
            case TextBoxBase textbox:
                textbox.BorderStyle = BorderStyle.FixedSingle;
                textbox.BackColor = Input;
                textbox.ForeColor = TextColor;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Input;
                combo.ForeColor = TextColor;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += (_, e) => DrawChoice(combo, e);
                break;
            case NumericUpDown number:
                number.BorderStyle = BorderStyle.FixedSingle;
                number.BackColor = Input;
                number.ForeColor = TextColor;
                break;
        }
    }

    private static void DrawChoice(ComboBox combo, DrawItemEventArgs e)
    {
        bool highlighted = e.State.HasFlag(DrawItemState.Selected) && !e.State.HasFlag(DrawItemState.ComboBoxEdit);
        using var background = new SolidBrush(highlighted ? Surface : Input);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (e.Index < 0 || e.Index >= combo.Items.Count)
            return;
        Rectangle bounds = Rectangle.Inflate(e.Bounds, -4, 0);
        TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds,
            combo.Enabled ? TextColor : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }

    private static TableLayoutPanel FieldRow(Control main, Control secondary, int secondaryWidth)
    {
        var line = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
            RowCount = 1, ColumnCount = 3, Margin = Padding.Empty
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 9));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, secondaryWidth));
        main.Dock = secondary.Dock = DockStyle.Top;
        line.Controls.Add(main, 0, 0);
        line.Controls.Add(secondary, 2, 0);
        return line;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> StyledButtons = new();

    private static void StyleButton(Button button, bool primary = false)
    {
        button.AutoSize = false;
        button.Height = 38;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Input;
        button.BackColor = primary ? Accent : Background;
        button.ForeColor = primary ? Background : TextColor;
        _ = StyledButtons.GetValue(button, static item =>
        {
            item.Paint += (_, e) =>
            {
                if (item.Enabled) return;
                using var background = new SolidBrush(Input);
                e.Graphics.FillRectangle(background, item.ClientRectangle);
                TextRenderer.DrawText(e.Graphics, item.Text, item.Font, item.ClientRectangle, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            };
            return new object();
        });
    }

    private sealed class VideoSurface : Control
    {
        private VideoFrame? frame;
        private bool unpainted;
        private string placeholder = "Conecta con el anfitrión para ver su pantalla";
        internal Size? FrameSize => frame?.Size;
        internal Action<byte[]>? InputSink;
        internal volatile GuestDevices DesktopDevices;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool CaptureInput
        {
            get => DesktopDevices != GuestDevices.None;
            set => DesktopDevices = value ? GuestDevices.Desktop : GuestDevices.None;
        }
        private bool CaptureMouse => DesktopDevices.HasFlag(GuestDevices.Mouse);
        private bool CaptureKeyboard => DesktopDevices.HasFlag(GuestDevices.Keyboard);
        private Rectangle videoBounds;
        private (ushort X, ushort Y)? lastMousePosition;
        internal event Action? Presented;
        internal void SetPlaceholder(string value)
        {
            placeholder = value;
            Invalidate();
        }

        internal VideoSurface()
        {
            hookProc = OnKeyboardHook;
            DoubleBuffered = false;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.FromArgb(8, 12, 16);
            AccessibleName = "Pantalla remota";
        }

        internal void SetFrame(VideoFrame? next)
        {
            frame?.Dispose();
            frame = next;
            unpainted = next is not null;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // The frame and the letterbox bars cover everything, so erasing first would only flicker.
            if (frame is null)
                base.OnPaintBackground(pevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (frame is null)
            {
                TextRenderer.DrawText(e.Graphics, placeholder, Font, ClientRectangle, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            double scale = Math.Min((double)Width / frame.Width, (double)Height / frame.Height);
            int width = (int)(frame.Width * scale);
            int height = (int)(frame.Height * scale);
            var bounds = new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
            videoBounds = bounds;
            FillBars(e.Graphics, bounds);

            if (frame.PixelAddress != IntPtr.Zero)
                StretchFrame(e.Graphics, bounds);
            else
            {
                e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                e.Graphics.DrawImage(frame.Image, bounds);
            }

            if (unpainted)
            {
                unpainted = false;
                Trace.FramePainted();
                Presented?.Invoke();
            }
        }

        private void FillBars(Graphics graphics, Rectangle bounds)
        {
            using var brush = new SolidBrush(BackColor);
            if (bounds.Top > 0)
                graphics.FillRectangle(brush, 0, 0, Width, bounds.Top);
            if (Height > bounds.Bottom)
                graphics.FillRectangle(brush, 0, bounds.Bottom, Width, Height - bounds.Bottom);
            if (bounds.Left > 0)
                graphics.FillRectangle(brush, 0, bounds.Top, bounds.Left, bounds.Height);
            if (Width > bounds.Right)
                graphics.FillRectangle(brush, bounds.Right, bounds.Top, Width - bounds.Right, bounds.Height);
        }

        // StretchDIBits copies the pixels straight to the window, far cheaper than GDI+ DrawImage.
        private void StretchFrame(Graphics graphics, Rectangle bounds)
        {
            VideoFrame displayed = frame!;
            IntPtr hdc = graphics.GetHdc();
            try
            {
                var info = new BitmapInfoHeader
                {
                    size = 40, width = displayed.Width, height = -displayed.Height, planes = 1,
                    bitCount = displayed.BitsPerPixel
                };
                SetStretchBltMode(hdc, 3);
                StretchDIBits(hdc, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0,
                    displayed.Width, displayed.Height, displayed.PixelAddress, ref info, 0, 0x00CC0020);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int size, width, height;
            public short planes, bitCount;
            public int compression, sizeImage, xPixelsPerMeter, yPixelsPerMeter, colorsUsed, colorsImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest,
            int xSrc, int ySrc, int wSrc, int hSrc, IntPtr bits, ref BitmapInfoHeader info, uint usage, uint rop);

        private void Emit(byte[] packet) => InputSink?.Invoke(packet);

        private bool Normalize(Point at, out ushort x, out ushort y)
        {
            x = y = 0;
            if (videoBounds.Width < 2 || videoBounds.Height < 2 || !videoBounds.Contains(at))
                return false;
            x = (ushort)((at.X - videoBounds.X) * 65535L / (videoBounds.Width - 1));
            y = (ushort)((at.Y - videoBounds.Y) * 65535L / (videoBounds.Height - 1));
            return true;
        }

        private void EmitMove(Point at, bool force = false)
        {
            if (Normalize(at, out ushort x, out ushort y) && (force || lastMousePosition != (x, y)))
            {
                lastMousePosition = (x, y);
                Emit(InputFeed.Packet(InputFeed.Move, 0, x, y));
            }
        }

        private static int ButtonId(MouseButtons button) => button switch
        {
            MouseButtons.Left => 0, MouseButtons.Right => 1, MouseButtons.Middle => 2,
            MouseButtons.XButton1 => 3, MouseButtons.XButton2 => 4, _ => -1
        };

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (CaptureMouse && !InputFeed.IsOwnInputMessage)
                EmitMove(e.Location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
            int id = ButtonId(e.Button);
            if (CaptureMouse && id >= 0 && !InputFeed.IsOwnInputMessage)
            {
                EmitMove(e.Location, force: true);
                Emit(InputFeed.Packet(InputFeed.Button, (byte)(id | 0x80)));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int id = ButtonId(e.Button);
            if (CaptureMouse && id >= 0 && !InputFeed.IsOwnInputMessage)
                Emit(InputFeed.Packet(InputFeed.Button, (byte)id));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (CaptureMouse && !InputFeed.IsOwnInputMessage)
                Emit(InputFeed.Packet(InputFeed.Wheel, 0, unchecked((ushort)(short)e.Delta)));
        }

        // While the remote screen has focus and the keyboard is captured, system shortcuts (Win, Alt+Tab,
        // Alt+Esc, Ctrl+Esc, Alt+F4) go to the host instead of acting on this PC.
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        private readonly HookProc hookProc;
        private IntPtr hookHandle;

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookData
        {
            public uint vkCode, scanCode, flags, time;
            public UIntPtr extra;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetModuleHandle(string? name);

        private void InstallHook()
        {
            if (hookHandle != IntPtr.Zero)
                return;
            hookHandle = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
        }

        private void RemoveHook()
        {
            if (hookHandle == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(hookHandle);
            GC.KeepAlive(hookProc);
            hookHandle = IntPtr.Zero;
        }

        private IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && CaptureKeyboard && Focused)
            {
                var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                if (data.extra == unchecked((nuint)InputFeed.InjectedInputTag))
                    return CallNextHookEx(hookHandle, code, wParam, lParam);
                Keys key = (Keys)data.vkCode;
                bool alt = (data.flags & 0x20) != 0;
                bool control = (ModifierKeys & Keys.Control) != 0;
                bool system = key is Keys.LWin or Keys.RWin
                    || (alt && key is Keys.Tab or Keys.Escape or Keys.F4)
                    || (control && key == Keys.Escape);
                if (system)
                {
                    bool up = wParam == 0x101 || wParam == 0x105;
                    byte flags = (byte)((up ? 1 : 0) | (IsExtended(key) ? 2 : 0));
                    Emit(InputFeed.Packet(InputFeed.Key, flags, (ushort)key));
                    return 1;
                }
            }
            return CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            InstallHook();
        }

        protected override bool IsInputKey(Keys keyData) => CaptureKeyboard || base.IsInputKey(keyData);

        private static bool IsExtended(Keys key) => key is Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            or Keys.Divide or Keys.NumLock or Keys.RControlKey or Keys.RMenu or Keys.LWin or Keys.RWin;

        private bool EmitKey(KeyEventArgs e, bool up)
        {
            if (!CaptureKeyboard || e.KeyCode == Keys.None || InputFeed.IsOwnInputMessage)
                return false;
            byte flags = (byte)((up ? 1 : 0) | (IsExtended(e.KeyCode) ? 2 : 0));
            Emit(InputFeed.Packet(InputFeed.Key, flags, (ushort)e.KeyCode));
            e.Handled = e.SuppressKeyPress = true;
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!EmitKey(e, up: false))
                base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (!EmitKey(e, up: true))
                base.OnKeyUp(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            RemoveHook();
            if (CaptureInput)
                Emit(InputFeed.Packet(InputFeed.ReleaseAll));
        }

        protected override void Dispose(bool disposing)
        {
            RemoveHook();
            if (disposing)
                frame?.Dispose();
            base.Dispose(disposing);
        }
    }
}
