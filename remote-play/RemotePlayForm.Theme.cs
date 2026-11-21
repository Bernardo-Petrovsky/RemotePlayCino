using System.Drawing.Drawing2D;

namespace RemotePlay;

internal sealed partial class RemotePlayForm
{
    private readonly List<ThemeTile> themeTiles = [];

    private Control BuildAppearancePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 14, 24, 16) };
        TableLayoutPanel card = HostCard();
        card.Controls.Add(Heading("Apariencia"));
        card.Controls.Add(Note("Elige cómo se ve la aplicación. El cambio es inmediato y se recuerda la próxima vez.", card));
        var tiles = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), Padding = Padding.Empty, BackColor = Surface
        };
        // Constrain before measuring: otherwise an autosized flow can request a single, very wide row.
        card.SizeChanged += (_, _) => tiles.MaximumSize =
            new Size(Math.Max(1, card.ClientSize.Width - card.Padding.Horizontal), 0);
        foreach ((ThemeChoice choice, string title, string detail) in new[]
        {
            (ThemeChoice.Dark, "Café", "Fondo oscuro, cómodo de noche"),
            (ThemeChoice.Light, "Leche", "Fondo claro, ideal con luz de día"),
            (ThemeChoice.Ocean, "Océano", "Azul nocturno y acentos celestes"),
            (ThemeChoice.Forest, "Bosque", "Verde profundo y tonos suaves"),
            (ThemeChoice.Graphite, "Grafito", "Grises oscuros y ámbar cálido"),
            (ThemeChoice.Plum, "Ciruela", "Violeta oscuro y acentos lilas"),
            (ThemeChoice.Sand, "Arena", "Crema y caramelo, cálido y claro"),
            (ThemeChoice.Lavender, "Lavanda", "Lila claro y violeta intenso"),
            (ThemeChoice.System, "Como Windows", "Sigue el modo de tu equipo")
        })
        {
            var tile = new ThemeTile(choice, title, detail, ApplyTheme);
            themeTiles.Add(tile);
            tiles.Controls.Add(tile);
        }
        card.Controls.Add(tiles);
        var column = Stack(Background, Padding.Empty);
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.Dock = DockStyle.Top;
        column.Controls.Add(card);
        column.Controls.Add(BuildUpdateCard());
        page.Controls.Add(column);
        return page;
    }

    private void ApplyTheme(ThemeChoice choice)
    {
        ThemePalette previous = Theme.Select(choice);
        SuspendLayout();
        Theme.Recolor(this, previous, Theme.Current);
        if (viewer is not null)
        {
            Theme.Recolor(viewer, previous, Theme.Current);
            Theme.StyleWindow(viewer);
        }
        ResumeLayout(true);
        Theme.StyleWindow(this);
        foreach (ThemeTile tile in themeTiles)
            tile.Invalidate();
        Invalidate(true);
    }

    /// <summary>A selectable miniature of one style, so the choice can be judged before it is made.</summary>
    private sealed class ThemeTile : Control
    {
        private const int PreviewHeight = 104;
        private readonly ThemeChoice choice;
        private readonly string title;
        private readonly string detail;
        private readonly Action<ThemeChoice> chosen;
        private bool hovered;

        internal ThemeTile(ThemeChoice choice, string title, string detail, Action<ThemeChoice> chosen)
        {
            this.choice = choice;
            this.title = title;
            this.detail = detail;
            this.chosen = chosen;
            Size = new Size(210, 188);
            Margin = new Padding(0, 0, 16, 12);
            TabStop = true;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            AccessibleName = $"Estilo {title}";
            AccessibleDescription = detail;
            AccessibleRole = AccessibleRole.RadioButton;
        }

        private bool Selected => Theme.Choice == choice;

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Focus(); chosen(choice); base.OnClick(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                chosen(choice);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var preview = new Rectangle(1, 1, Width - 3, PreviewHeight);
            using GraphicsPath shape = Rounded(preview, 10);
            GraphicsState state = g.Save();
            g.SetClip(shape);
            if (choice == ThemeChoice.System)
            {
                g.SetClip(new Rectangle(preview.X, preview.Y, preview.Width / 2, preview.Height), CombineMode.Intersect);
                DrawMiniature(g, preview, ThemePalette.Cafe);
                g.Restore(state);
                state = g.Save();
                g.SetClip(shape);
                g.SetClip(new Rectangle(preview.X + preview.Width / 2, preview.Y, preview.Width - preview.Width / 2, preview.Height), CombineMode.Intersect);
                DrawMiniature(g, preview, ThemePalette.Leche);
            }
            else
            {
                DrawMiniature(g, preview, Theme.Resolve(choice));
            }
            g.Restore(state);

            using var border = new Pen(Selected ? Accent : hovered ? Muted : Outline, Selected ? 2.5f : 1.2f);
            g.DrawPath(border, shape);
            if (Focused)
            {
                using var focus = new Pen(TextColor) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
            }

            var titleBounds = new Rectangle(2, PreviewHeight + 10, Width - 4, 24);
            string caption = Selected ? title + "  ✓" : title;
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, caption, bold, titleBounds, Selected ? Accent : TextColor,
                TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using var small = new Font(Font.FontFamily, 9.5f);
            TextRenderer.DrawText(g, detail, small, new Rectangle(2, PreviewHeight + 34, Width - 4, 34), Muted,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        private static void DrawMiniature(Graphics g, Rectangle area, ThemePalette p)
        {
            using (var back = new SolidBrush(p.Background)) g.FillRectangle(back, area);
            using (var bar = new SolidBrush(p.Surface)) g.FillRectangle(bar, area.X, area.Y, area.Width, 20);
            using (var dot = new SolidBrush(p.Accent)) g.FillEllipse(dot, area.X + 9, area.Y + 5, 10, 10);
            using (var tab = new SolidBrush(p.Input))
            {
                g.FillRectangle(tab, area.Right - 78, area.Y + 6, 30, 8);
                g.FillRectangle(tab, area.Right - 42, area.Y + 6, 30, 8);
            }
            var card = new Rectangle(area.X + 10, area.Y + 30, area.Width - 20, 44);
            using (var surface = new SolidBrush(p.Surface)) g.FillRectangle(surface, card);
            using (var edge = new Pen(p.Outline)) g.DrawRectangle(edge, card);
            using (var text = new SolidBrush(p.Text)) g.FillRectangle(text, card.X + 8, card.Y + 9, 62, 6);
            using (var muted = new SolidBrush(p.Muted)) g.FillRectangle(muted, card.X + 8, card.Y + 22, 98, 5);
            using (var input = new SolidBrush(p.Input)) g.FillRectangle(input, card.X + 8, card.Y + 32, 70, 7);
            using (var accent = new SolidBrush(p.Accent)) g.FillRectangle(accent, area.Right - 70, area.Bottom - 24, 56, 14);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseAllFigures();
            return path;
        }
    }
}
