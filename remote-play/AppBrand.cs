namespace RemotePlay;

internal static class AppBrand
{
    internal const string Name = "RemotePlayCino";
    internal const string ShortName = Name;

    private static Stream OpenAsset(string name) => typeof(AppBrand).Assembly.GetManifestResourceStream("RemotePlay.Assets." + name)
        ?? throw new InvalidOperationException("No se encuentra la imagen de " + Name + ".");

    internal static Icon CreateIcon()
    {
        using Stream stream = OpenAsset("GamepadCino.ico");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    internal static void ApplyIcon(Form window)
    {
        Icon icon = CreateIcon();
        window.Icon = icon;
        window.Disposed += (_, _) => icon.Dispose();
        Theme.StyleWindow(window);
    }

    internal static Image CreateLogo()
    {
        using Stream stream = OpenAsset("GamepadCino.png");
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
