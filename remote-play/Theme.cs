using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemotePlay;

internal enum ThemeChoice { Dark, Light, System, Ocean, Forest, Graphite, Plum, Sand, Lavender }

/// <summary>All palettes share color roles so a theme switch is a one-to-one remap.</summary>
internal sealed record ThemePalette(
    Color Background, Color Surface, Color Input, Color Text, Color Muted, Color Accent,
    Color AccentHover, Color Outline, Color Danger, Color Selected, bool IsDark)
{
    // Espresso browns and the teal of the logo's steam: the dark style.
    internal static readonly ThemePalette Cafe = new(
        Color.FromArgb(26, 20, 16), Color.FromArgb(38, 29, 23), Color.FromArgb(53, 41, 31),
        Color.FromArgb(247, 235, 220), Color.FromArgb(205, 187, 167), Color.FromArgb(47, 179, 145),
        Color.FromArgb(76, 203, 169), Color.FromArgb(70, 54, 43), Color.FromArgb(255, 154, 143),
        Color.FromArgb(31, 59, 52), true);

    // Milk foam and deep teal: the light style. Text and accent keep at least 4.5:1 contrast on their surfaces.
    internal static readonly ThemePalette Leche = new(
        Color.FromArgb(242, 245, 243), Color.FromArgb(255, 255, 255), Color.FromArgb(232, 238, 235),
        Color.FromArgb(35, 28, 23), Color.FromArgb(91, 102, 96), Color.FromArgb(15, 122, 99),
        Color.FromArgb(11, 99, 80), Color.FromArgb(203, 214, 209), Color.FromArgb(179, 38, 30),
        Color.FromArgb(213, 237, 229), false);

    internal static readonly ThemePalette Ocean = new(
        Color.FromArgb(14, 23, 38), Color.FromArgb(21, 34, 52), Color.FromArgb(30, 48, 70),
        Color.FromArgb(234, 244, 255), Color.FromArgb(170, 194, 217), Color.FromArgb(88, 184, 255),
        Color.FromArgb(128, 204, 255), Color.FromArgb(54, 77, 102), Color.FromArgb(255, 158, 163),
        Color.FromArgb(26, 65, 93), true);

    internal static readonly ThemePalette Forest = new(
        Color.FromArgb(16, 27, 23), Color.FromArgb(23, 40, 33), Color.FromArgb(33, 54, 44),
        Color.FromArgb(237, 247, 234), Color.FromArgb(178, 204, 184), Color.FromArgb(132, 210, 145),
        Color.FromArgb(166, 230, 176), Color.FromArgb(52, 81, 63), Color.FromArgb(255, 161, 151),
        Color.FromArgb(39, 77, 52), true);

    internal static readonly ThemePalette Graphite = new(
        Color.FromArgb(22, 24, 29), Color.FromArgb(31, 34, 41), Color.FromArgb(43, 47, 56),
        Color.FromArgb(243, 244, 247), Color.FromArgb(190, 195, 207), Color.FromArgb(255, 195, 100),
        Color.FromArgb(255, 214, 148), Color.FromArgb(65, 71, 84), Color.FromArgb(255, 151, 160),
        Color.FromArgb(68, 53, 35), true);

    internal static readonly ThemePalette Plum = new(
        Color.FromArgb(29, 20, 37), Color.FromArgb(42, 29, 51), Color.FromArgb(58, 40, 70),
        Color.FromArgb(249, 238, 255), Color.FromArgb(207, 181, 221), Color.FromArgb(211, 155, 255),
        Color.FromArgb(228, 188, 255), Color.FromArgb(80, 56, 96), Color.FromArgb(255, 159, 184),
        Color.FromArgb(72, 42, 88), true);

    internal static readonly ThemePalette Sand = new(
        Color.FromArgb(247, 241, 229), Color.FromArgb(255, 252, 245), Color.FromArgb(235, 225, 207),
        Color.FromArgb(53, 42, 28), Color.FromArgb(111, 91, 65), Color.FromArgb(138, 77, 18),
        Color.FromArgb(111, 60, 12), Color.FromArgb(208, 189, 157), Color.FromArgb(177, 44, 37),
        Color.FromArgb(243, 220, 182), false);

    internal static readonly ThemePalette Lavender = new(
        Color.FromArgb(244, 241, 250), Color.FromArgb(255, 253, 255), Color.FromArgb(233, 226, 243),
        Color.FromArgb(48, 35, 65), Color.FromArgb(105, 86, 128), Color.FromArgb(114, 67, 164),
        Color.FromArgb(92, 49, 141), Color.FromArgb(202, 188, 221), Color.FromArgb(175, 40, 77),
        Color.FromArgb(226, 210, 243), false);
}

internal static class Theme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static string ChoiceFile => Path.Combine(AppStorage.ProfileDirectory, "estilo.txt");

    internal static ThemeChoice Choice { get; private set; } = ThemeChoice.Dark;
    internal static ThemePalette Current { get; private set; } = ThemePalette.Cafe;

    // Loaded on first use, which is before any control reads a color.
    static Theme()
    {
        try
        {
            if (Enum.TryParse(File.ReadAllText(ChoiceFile).Trim(), true, out ThemeChoice saved) && Enum.IsDefined(saved))
                Choice = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Note($"No se pudo leer el estilo guardado: {ex.Message}");
        }
        Current = Resolve(Choice);
    }

    internal static ThemePalette Resolve(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => ThemePalette.Leche,
        ThemeChoice.System => WindowsUsesLightApps() ? ThemePalette.Leche : ThemePalette.Cafe,
        ThemeChoice.Ocean => ThemePalette.Ocean,
        ThemeChoice.Forest => ThemePalette.Forest,
        ThemeChoice.Graphite => ThemePalette.Graphite,
        ThemeChoice.Plum => ThemePalette.Plum,
        ThemeChoice.Sand => ThemePalette.Sand,
        ThemeChoice.Lavender => ThemePalette.Lavender,
        _ => ThemePalette.Cafe
    };

    private static bool WindowsUsesLightApps()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Applies a choice, remembers it and returns the palette that was active before.</summary>
    internal static ThemePalette Select(ThemeChoice choice)
    {
        ThemePalette previous = Current;
        Choice = choice;
        Current = Resolve(choice);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)!);
            File.WriteAllText(ChoiceFile, choice.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.Note($"No se pudo guardar el estilo: {ex.Message}");
        }
        return previous;
    }

    /// <summary>Repaints a window in place: every color that matches a role of the old palette moves to the new one.</summary>
    internal static void Recolor(Control root, ThemePalette from, ThemePalette to)
    {
        if (from == to)
            return;
        var map = new Dictionary<Color, Color>
        {
            [from.Background] = to.Background, [from.Surface] = to.Surface, [from.Input] = to.Input,
            [from.Text] = to.Text, [from.Muted] = to.Muted, [from.Accent] = to.Accent,
            [from.AccentHover] = to.AccentHover, [from.Outline] = to.Outline, [from.Danger] = to.Danger,
            [from.Selected] = to.Selected
        };
        Walk(root, map);
    }

    private static void Walk(Control control, Dictionary<Color, Color> map)
    {
        Color Remap(Color color) => map.TryGetValue(color, out Color next) ? next : color;

        control.BackColor = Remap(control.BackColor);
        control.ForeColor = Remap(control.ForeColor);
        switch (control)
        {
            case ButtonBase button:
                button.FlatAppearance.BorderColor = Remap(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = Remap(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = Remap(button.FlatAppearance.MouseDownBackColor);
                button.FlatAppearance.CheckedBackColor = Remap(button.FlatAppearance.CheckedBackColor);
                break;
        }
        control.Invalidate();
        foreach (Control child in control.Controls)
            Walk(child, map);
    }

    /// <summary>Makes the title bar match the style: dark mode, plus the caption colors on Windows 11.</summary>
    internal static void StyleWindow(Form window)
    {
        if (!window.IsHandleCreated)
        {
            window.HandleCreated += (_, _) => StyleWindow(window);
            return;
        }
        ThemePalette palette = Current;
        int dark = palette.IsDark ? 1 : 0;
        int caption = ToColorRef(palette.Surface);
        int text = ToColorRef(palette.Text);
        // Older Windows versions reject these attributes; the standard title bar stays, which is fine.
        _ = DwmSetWindowAttribute(window.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(window.Handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(window.Handle, 36, ref text, sizeof(int));
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
