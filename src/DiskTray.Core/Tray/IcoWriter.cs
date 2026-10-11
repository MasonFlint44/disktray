using System.Buffers.Binary;

namespace DiskTray.Core.Tray;

/// <summary>Writes a single-image .ico file from 32-bit pixels.</summary>
public static class IcoWriter
{
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    /// <param name="bgra">Straight (not premultiplied) alpha BGRA pixels, top row first.</param>
    public static byte[] Write(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width is < 1 or > 256 || height is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Icons are 1–256 pixels on each side.");
        }

        int stride = width * 4;
        if (bgra.Length != stride * height)
        {
            throw new ArgumentException($"Expected {stride * height} bytes of BGRA pixels, got {bgra.Length}.", nameof(bgra));
        }

        // The AND mask is 1 bit per pixel with rows padded to 4 bytes. It's all zeros
        // because the alpha channel already says what's transparent.
        int maskStride = (width + 31) / 32 * 4;
        int imageSize = BitmapInfoHeaderSize + (stride * height) + (maskStride * height);
        var ico = new byte[IconDirSize + IconDirEntrySize + imageSize];
        var span = ico.AsSpan();

        // ICONDIR: reserved, type 1 = icon, one image.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);

        // ICONDIRENTRY: 0 means 256.
        var entry = span[IconDirSize..];
        entry[0] = (byte)(width == 256 ? 0 : width);
        entry[1] = (byte)(height == 256 ? 0 : height);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(entry[8..], imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(entry[12..], IconDirSize + IconDirEntrySize);

        // BITMAPINFOHEADER: height counts the color rows and the mask rows.
        var header = span[(IconDirSize + IconDirEntrySize)..];
        BinaryPrimitives.WriteInt32LittleEndian(header, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], imageSize - BitmapInfoHeaderSize);

        // Bitmap rows go bottom-up.
        var pixels = header[BitmapInfoHeaderSize..];
        for (int y = 0; y < height; y++)
        {
            bgra.Slice(y * stride, stride).CopyTo(pixels[((height - 1 - y) * stride)..]);
        }

        return ico;
    }
}
