using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RemotePlay;

internal sealed class ScreenCapture : IDisposable
{
    internal const int MaxWidth = 960;
    internal const int MaxHeight = 540;
    private static readonly ImageCodecInfo Jpeg =
        ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters jpegOptions = new(1);
    private readonly MemoryStream output = new();
    private Bitmap? desktop;
    private Bitmap? scaled;

    internal ScreenCapture() => jpegOptions.Param[0] = new EncoderParameter(Encoder.Quality, 65L);

    internal ReadOnlyMemory<byte> Grab()
    {
        Rectangle bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new IOException("Windows no tiene una pantalla principal disponible.");
        if (desktop?.Size != bounds.Size)
        {
            desktop?.Dispose();
            scaled?.Dispose();
            desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            double scale = Math.Min(1, Math.Min((double)MaxWidth / bounds.Width, (double)MaxHeight / bounds.Height));
            scaled = new Bitmap(Math.Max(1, (int)(bounds.Width * scale)),
                Math.Max(1, (int)(bounds.Height * scale)), PixelFormat.Format24bppRgb);
        }

        using (Graphics capture = Graphics.FromImage(desktop))
            capture.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        using (Graphics resize = Graphics.FromImage(scaled!))
        {
            resize.InterpolationMode = InterpolationMode.Bilinear;
            resize.DrawImage(desktop, 0, 0, scaled!.Width, scaled.Height);
        }

        output.SetLength(0);
        scaled.Save(output, Jpeg, jpegOptions);
        return output.GetBuffer().AsMemory(0, (int)output.Length);
    }

    public void Dispose()
    {
        desktop?.Dispose();
        scaled?.Dispose();
        output.Dispose();
        jpegOptions.Dispose();
    }
}
