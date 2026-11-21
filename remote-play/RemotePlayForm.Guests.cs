using System.Drawing.Drawing2D;

namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly Dictionary<GuestSession, Control> guestCards = [];
    private readonly FlowLayoutPanel guestList = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Margin = Padding.Empty
    };
    private readonly Label guestCount = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label emptyGuests = new()
    {
        Text = "Comparte la IP y el código para invitar a alguien.", AutoSize = true, ForeColor = Muted, Padding = new Padding(2, 12, 2, 4)
    };

    private Control BuildGuestPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Surface,
            Padding = new Padding(18), Margin = Padding.Empty, MinimumSize = new Size(0, 245)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(Heading("Invitados"), 0, 0);
        panel.Controls.Add(guestCount, 0, 1);
        guestList.Controls.Add(emptyGuests);
        guestList.SizeChanged += (_, _) =>
        {
            foreach (Control card in guestCards.Values)
                card.Width = GuestCardWidth();
            emptyGuests.MaximumSize = new Size(GuestCardWidth(), 0);
        };
        panel.Controls.Add(guestList, 0, 2);
        RefreshGuestCount();
        return panel;
    }

    private int GuestCardWidth() => Math.Max(190, guestList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);

    private void SetGuest(GuestSession guest, bool connected)
    {
        if (connected && !guestCards.ContainsKey(guest))
        {
            Control card = CreateGuestCard(guest);
            guestCards.Add(guest, card);
            guestList.Controls.Add(card);
        }
        else if (!connected && guestCards.Remove(guest, out Control? card))
        {
            guestList.Controls.Remove(card);
            card.Dispose();
        }
        RefreshGuestCount();
    }

    private void ClearGuests()
    {
        foreach (Control card in guestCards.Values)
        {
            guestList.Controls.Remove(card);
            card.Dispose();
        }
        guestCards.Clear();
        RefreshGuestCount();
    }

    private void RefreshGuestCount()
    {
        emptyGuests.Visible = guestCards.Count == 0;
        guestCount.Text = $"{guestCards.Count} de {GuestSession.Limit} conectados";
        hostPad.Visible = hostVideo.Visible = hostExtras.Visible = guestCards.Count <= 1;
        if (guestCards.Count == 0)
        {
            hostPad.Text = "Sin mando remoto";
            hostVideo.Text = hostExtras.Text = "";
        }
    }

    private Control CreateGuestCard(GuestSession guest)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 3, Width = GuestCardWidth(), Height = 128,
            Margin = new Padding(0, 10, 0, 0), Padding = new Padding(10), BackColor = Background
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var avatar = new Panel { Width = 34, Height = 34, Margin = Padding.Empty };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Accent);
            e.Graphics.FillEllipse(brush, 1, 1, 30, 30);
            TextRenderer.DrawText(e.Graphics, System.Globalization.StringInfo.GetNextTextElement(guest.Name).ToUpperInvariant(), avatar.Font,
                new Rectangle(1, 1, 30, 30), Background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        identity.Controls.Add(avatar, 0, 0);
        identity.SetRowSpan(avatar, 2);
        identity.Controls.Add(new Label
        {
            Text = guest.Name, AutoEllipsis = true, UseMnemonic = false, Dock = DockStyle.Fill, Margin = Padding.Empty
        }, 1, 0);
        var online = new Label { Text = $"Conectado · {guest.Endpoint}", ForeColor = Accent, AutoSize = true, Margin = Padding.Empty };
        identity.Controls.Add(online, 1, 1);
        card.Controls.Add(identity, 0, 0);

        var permissions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        GuestDevices initial;
        lock (guest.Gate)
            initial = guest.Permissions;
        AddPermission("Mando", GuestDevices.Pad, true);
        AddPermission("Teclado", GuestDevices.Keyboard, guest.CanManageDesktop);
        AddPermission("Mouse", GuestDevices.Mouse, guest.CanManageDesktop);
        card.Controls.Add(permissions, 0, 1);
        var kick = new Button { Text = "Expulsar", AutoSize = true, AccessibleName = $"Expulsar a {guest.Name}", Margin = Padding.Empty };
        StyleButton(kick);
        kick.ForeColor = Danger;
        kick.Click += async (_, _) =>
        {
            kick.Enabled = permissions.Enabled = false;
            online.Text = "Desconectando…";
            try { await guest.KickAsync(); }
            catch (AggregateException ex) { Trace.Note($"Error al expulsar a un invitado: {ex.Message}"); }
        };
        card.Controls.Add(kick, 0, 2);
        return card;

        void AddPermission(string caption, GuestDevices device, bool available)
        {
            var toggle = new CheckBox
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = true, Enabled = available,
                AccessibleName = $"{caption} de {guest.Name}", Margin = new Padding(0, 3, 5, 1),
                Checked = available && initial.HasFlag(device)
            };
            toggle.FlatAppearance.BorderColor = Muted;
            toggle.FlatAppearance.CheckedBackColor = SelectedTint;
            toggle.FlatAppearance.MouseOverBackColor = Surface;
            void Update()
            {
                toggle.ForeColor = toggle.Checked ? Accent : Muted;
                toggle.Text = (toggle.Checked ? "✓ " : "× ") + caption;
            }
            Update();
            toggle.CheckedChanged += (_, _) =>
            {
                guest.SetPermission(device, toggle.Checked);
                Update();
            };
            permissions.Controls.Add(toggle);
        }
    }
}
