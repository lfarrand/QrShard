using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// A plain RGB pixel grid — the working representation shared by the decode pipeline and the
/// camera rectifier — with the two sampling primitives every consumer needs.
/// </summary>
internal sealed class Bitmap(Rgb24[] px, int width, int height)
{
    public const int DarkThreshold = 80; // luminance below this is "frame black"

    public readonly Rgb24[] Px = px;
    public readonly int Width = width;
    public readonly int Height = height;

    public Rgb24 At(int x, int y) => Px[y * Width + x];

    public bool IsDark(int x, int y)
    {
        var p = Px[y * Width + x];
        return p.R + p.G + p.B < DarkThreshold * 3;
    }

    /// <summary>
    /// Average color over a (2rx+1) x (2ry+1) pixel box around the (continuous,
    /// boundary-convention) position; the containing pixel is floor(coordinate).
    /// </summary>
    public Rgb24 SampleBox(double cx, double cy, int rx, int ry)
    {
        int x0 = Math.Clamp((int)Math.Floor(cx) - rx, 0, Width - 1);
        int x1 = Math.Clamp((int)Math.Floor(cx) + rx, 0, Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(cy) - ry, 0, Height - 1);
        int y1 = Math.Clamp((int)Math.Floor(cy) + ry, 0, Height - 1);
        long r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var p = At(x, y);
                r += p.R;
                g += p.G;
                b += p.B;
                n++;
            }
        }
        return new Rgb24((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>Bilinear sample at a fractional pixel position; outside the image reads as white.</summary>
    public Rgb24 SampleBilinear(double x, double y)
    {
        if (x < -1 || y < -1 || x > Width || y > Height)
            return new Rgb24(255, 255, 255); // outside the photo: treat as quiet-zone white

        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        var p00 = ClampedAt(x0, y0);
        var p10 = ClampedAt(x0 + 1, y0);
        var p01 = ClampedAt(x0, y0 + 1);
        var p11 = ClampedAt(x0 + 1, y0 + 1);

        return new Rgb24(
            Mix(p00.R, p10.R, p01.R, p11.R, fx, fy),
            Mix(p00.G, p10.G, p01.G, p11.G, fx, fy),
            Mix(p00.B, p10.B, p01.B, p11.B, fx, fy));

        static byte Mix(byte a, byte b, byte c, byte d, double fx, double fy)
        {
            double top = a + (b - a) * fx;
            double bottom = c + (d - c) * fx;
            return (byte)Math.Clamp(top + (bottom - top) * fy + 0.5, 0, 255);
        }
    }

    private Rgb24 ClampedAt(int x, int y) => At(Math.Clamp(x, 0, Width - 1), Math.Clamp(y, 0, Height - 1));
}
