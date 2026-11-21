using System.Buffers;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemotePlay;

internal sealed class VideoFrame : IDisposable
{
    private readonly ArrayPool<byte>? pool;
    private readonly byte[]? pixels;
    private Bitmap? image;
    private GCHandle pin;
    private int disposed;

    internal VideoFrame(Bitmap image)
    {
        this.image = image;
        Size = image.Size;
    }

    internal VideoFrame(ArrayPool<byte> pool, byte[] pixels, Size size, bool packedRgb)
    {
        this.pool = pool;
        this.pixels = pixels;
        Size = size;
        BitsPerPixel = packedRgb ? (short)24 : (short)32;
        pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        PixelAddress = pin.AddrOfPinnedObject();
    }

    // Raw frames are drawn from PixelAddress. Only callers that need a Bitmap create one.
    internal Bitmap Image
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return image ??= new Bitmap(Width, Height, Width * (BitsPerPixel / 8),
                BitsPerPixel == 24 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppPArgb, PixelAddress);
        }
    }
    internal IntPtr PixelAddress { get; }
    internal short BitsPerPixel { get; }
    internal Size Size { get; }
    internal int Width => Size.Width;
    internal int Height => Size.Height;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { image?.Dispose(); }
        finally
        {
            if (pin.IsAllocated)
                pin.Free();
            if (pixels is not null)
                pool!.Return(pixels);
        }
    }
}
