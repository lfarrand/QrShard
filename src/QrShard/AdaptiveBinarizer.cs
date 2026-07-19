namespace QrShard;

/// <summary>
/// Integral-image adaptive threshold: photos have illumination gradients (screen falloff,
/// glare) that defeat a single global threshold.
/// </summary>
internal sealed class AdaptiveBinarizer : IAdaptiveBinarizer
{
    public bool[] Threshold(Bitmap photo)
    {
        int w = photo.Width, h = photo.Height;
        var lum = new byte[w * h];
        for (int i = 0; i < lum.Length; i++)
        {
            var p = photo.Px[i];
            lum[i] = (byte)((p.R + p.G + p.B) / 3);
        }

        var integral = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += lum[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        int window = Math.Max(15, Math.Min(w, h) / 16);
        var dark = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - window), y1 = Math.Min(h, y + window);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - window), x1 = Math.Min(w, x + window);
                long sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1]
                         - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
                long area = (long)(x1 - x0) * (y1 - y0);
                dark[y * w + x] = lum[y * w + x] * area * 5 < sum * 4; // below 80% of local mean
            }
        }
        return dark;
    }
}
