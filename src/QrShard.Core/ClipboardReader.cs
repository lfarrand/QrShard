using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Reads a bitmap straight off the Windows clipboard (`decode --clipboard`): Win+Shift+S a
/// displayed shard on the receiving side and decode it without ever saving a file. The CF_DIB
/// parser is a pure function over the packed bytes, so it unit-tests without a clipboard.
/// </summary>
internal sealed class ClipboardReader
{
    private const uint CfDib = 8;

    [SupportedOSPlatform("windows")]
    public unsafe Bitmap? TryRead()
    {
        bool opened = false;
        for (int attempt = 0; attempt < 5 && !(opened = OpenClipboard(IntPtr.Zero)); attempt++)
            Thread.Sleep(50); // another process may briefly hold the clipboard
        if (!opened)
            return null;
        try
        {
            if (!IsClipboardFormatAvailable(CfDib))
                return null;
            IntPtr handle = GetClipboardData(CfDib);
            if (handle == IntPtr.Zero)
                return null;
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero)
                return null;
            try
            {
                long size = (long)GlobalSize(handle);
                if (size is <= 0 or > int.MaxValue)
                    return null;
                return ParseDib(new ReadOnlySpan<byte>((void*)data, (int)size));
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Parses a packed CF_DIB (BITMAPINFOHEADER + pixels): 24/32-bit uncompressed
    /// (BI_RGB, or BI_BITFIELDS with the standard BGRA masks), top-down or bottom-up.</summary>
    internal static Bitmap? ParseDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
            return null;
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || rawHeight == 0 || (bitCount != 24 && bitCount != 32))
            return null;
        if (compression != 0 && compression != 3) // BI_RGB or BI_BITFIELDS
            return null;
        if ((long)width * Math.Abs(rawHeight) > 500_000_000)
            return null;

        bool bottomUp = rawHeight > 0;
        int height = Math.Abs(rawHeight);
        int bytesPerPixel = bitCount / 8;
        int masks = compression == 3 && headerSize == 40 ? 12 : 0;
        long offset = headerSize + masks + (long)clrUsed * 4;
        int stride = (width * bytesPerPixel + 3) & ~3;
        if (offset + (long)stride * height > dib.Length)
            return null;

        var px = new Rgb24[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceY = bottomUp ? height - 1 - y : y;
            var row = dib.Slice((int)(offset + (long)sourceY * stride), width * bytesPerPixel);
            for (int x = 0; x < width; x++)
            {
                int i = x * bytesPerPixel;
                px[y * width + x] = new Rgb24(row[i + 2], row[i + 1], row[i]); // BGR(A) → RGB
            }
        }
        return new Bitmap(px, width, height);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);
}
