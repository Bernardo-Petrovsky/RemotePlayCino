using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RemotePlay;

internal static class Nv12Conversion
{
    // NV12 has one luma byte per pixel and one interleaved U,V pair per 2x2 block.
    // The decoder supplies limited-range BT.601. Coefficients use six fractional bits.
    internal static void CopyToBgra(byte[] source, byte[] destination, int width, int height)
    {
        int lumaLength = checked(width * height);
        if (width <= 0 || width % 16 != 0 || height <= 0 || height % 2 != 0 ||
            source.Length != checked(lumaLength + lumaLength / 2) || destination.Length < checked(lumaLength * 4))
            throw new ArgumentException("Dimensiones o búferes NV12 inválidos.");

        if (height > 1080 && width % 32 == 0 && Avx2.IsSupported)
        {
            CopyWide(source, destination, width, height, lumaLength);
            return;
        }

        ref byte input = ref source[0];
        ref byte output = ref destination[0];
        var byteMask = Vector128.Create((ushort)255);
        var chromaOffset = Vector128.Create((short)128);
        for (int y = 0; y < height; y++)
        {
            int yStart = y * width;
            int uvStart = lumaLength + y / 2 * width;
            for (int x = 0; x < width; x += 16)
            {
                var luma = Vector128.LoadUnsafe(ref input, (nuint)(yStart + x));
                var uv = Vector128.LoadUnsafe(ref input, (nuint)(uvStart + x)).AsUInt16();
                var u = Sse2.Subtract(Sse2.And(uv, byteMask).AsInt16(), chromaOffset);
                var v = Sse2.Subtract(Sse2.ShiftRightLogical(uv, 8).AsInt16(), chromaOffset);
                var red = Sse2.MultiplyLow(v, Vector128.Create((short)102));
                var green = Sse2.Add(Sse2.MultiplyLow(u, Vector128.Create((short)-25)),
                    Sse2.MultiplyLow(v, Vector128.Create((short)-52)));
                var blue = Sse2.MultiplyLow(u, Vector128.Create((short)129));
                nuint offset = (nuint)((yStart + x) * 4);

                WriteEight(ref output, offset, Sse2.UnpackLow(luma, Vector128<byte>.Zero).AsInt16(),
                    Sse2.UnpackLow(red, red), Sse2.UnpackLow(green, green), Sse2.UnpackLow(blue, blue));
                WriteEight(ref output, offset + 32, Sse2.UnpackHigh(luma, Vector128<byte>.Zero).AsInt16(),
                    Sse2.UnpackHigh(red, red), Sse2.UnpackHigh(green, green), Sse2.UnpackHigh(blue, blue));
            }
        }
    }

    private static void WriteEight(ref byte output, nuint offset, Vector128<short> luma,
        Vector128<short> red, Vector128<short> green, Vector128<short> blue)
    {
        var y = Sse2.MultiplyLow(Sse2.Subtract(luma, Vector128.Create((short)16)), Vector128.Create((short)74));
        var rounded = Sse2.Add(y, Vector128.Create((short)32));
        var bg = Sse2.UnpackLow(ToBytes(rounded, blue), ToBytes(rounded, green)).AsUInt16();
        var ra = Sse2.UnpackLow(ToBytes(rounded, red), Vector128.Create(byte.MaxValue)).AsUInt16();
        Sse2.UnpackLow(bg, ra).AsByte().StoreUnsafe(ref output, offset);
        Sse2.UnpackHigh(bg, ra).AsByte().StoreUnsafe(ref output, offset + 16);
    }

    private static Vector128<byte> ToBytes(Vector128<short> rounded, Vector128<short> chroma) =>
        Sse2.PackUnsignedSaturate(Sse2.ShiftRightArithmetic(Sse2.AddSaturate(rounded, chroma), 6),
            Vector128<short>.Zero);

    private static void CopyWide(byte[] source, byte[] destination, int width, int height, int lumaLength)
    {
        ref byte input = ref source[0];
        ref byte output = ref destination[0];
        var byteMask = Vector256.Create((ushort)255);
        var chromaOffset = Vector256.Create((short)128);

        // Each pair of luma rows shares one chroma row.
        for (int y = 0; y < height; y += 2)
        {
            int row = y * width;
            int chromaRow = lumaLength + y / 2 * width;
            for (int x = 0; x < width; x += 32)
            {
                var uv = Vector256.LoadUnsafe(ref input, (nuint)(chromaRow + x)).AsUInt16();
                var u = Avx2.Subtract(Avx2.And(uv, byteMask).AsInt16(), chromaOffset);
                var v = Avx2.Subtract(Avx2.ShiftRightLogical(uv, 8).AsInt16(), chromaOffset);
                var red = Avx2.MultiplyLow(v, Vector256.Create((short)102));
                var green = Avx2.Add(Avx2.MultiplyLow(u, Vector256.Create((short)-25)),
                    Avx2.MultiplyLow(v, Vector256.Create((short)-52)));
                var blue = Avx2.MultiplyLow(u, Vector256.Create((short)129));
                var lower = (Red: Avx2.UnpackLow(red, red), Green: Avx2.UnpackLow(green, green), Blue: Avx2.UnpackLow(blue, blue));
                var upper = (Red: Avx2.UnpackHigh(red, red), Green: Avx2.UnpackHigh(green, green), Blue: Avx2.UnpackHigh(blue, blue));
                for (int pixel = row + x; pixel <= row + x + width; pixel += width)
                {
                    var luma = Vector256.LoadUnsafe(ref input, (nuint)pixel);
                    nuint offset = (nuint)pixel * 4;
                    WriteWide(ref output, offset, Avx2.UnpackLow(luma, Vector256<byte>.Zero).AsInt16(),
                        lower.Red, lower.Green, lower.Blue);
                    WriteWide(ref output, offset + 32, Avx2.UnpackHigh(luma, Vector256<byte>.Zero).AsInt16(),
                        upper.Red, upper.Green, upper.Blue);
                }
            }
        }
    }

    private static void WriteWide(ref byte output, nuint offset, Vector256<short> luma,
        Vector256<short> red, Vector256<short> green, Vector256<short> blue)
    {
        var y = Avx2.MultiplyLow(Avx2.Subtract(luma, Vector256.Create((short)16)), Vector256.Create((short)74));
        var rounded = Avx2.Add(y, Vector256.Create((short)32));
        var bg = Avx2.UnpackLow(ToBytesWide(rounded, blue), ToBytesWide(rounded, green)).AsUInt16();
        var ra = Avx2.UnpackLow(ToBytesWide(rounded, red), Vector256.Create(byte.MaxValue)).AsUInt16();
        var first = Avx2.UnpackLow(bg, ra);
        var last = Avx2.UnpackHigh(bg, ra);
        Avx2.Permute2x128(first, last, 0x20).AsByte().StoreUnsafe(ref output, offset);
        Avx2.Permute2x128(first, last, 0x31).AsByte().StoreUnsafe(ref output, offset + 64);
    }

    private static Vector256<byte> ToBytesWide(Vector256<short> rounded, Vector256<short> chroma) =>
        Avx2.PackUnsignedSaturate(Avx2.ShiftRightArithmetic(Avx2.AddSaturate(rounded, chroma), 6),
            Vector256<short>.Zero);
}
