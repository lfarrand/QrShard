namespace QrShard;

/// <summary>
/// Sauvola local adaptive threshold: photos have illumination gradients (screen falloff, glare)
/// that defeat a single global threshold. Sauvola adds a local-CONTRAST term on top of the local
/// mean — T = mean·(1 + k·(std/R − 1)) — so a flat/glare-washed region (low std) keeps the
/// threshold near mean·(1−k) and stops classifying sensor noise as dark runs, while a
/// high-contrast region (bimodal black/white finder modules, std → R) raises the threshold toward
/// the local mean, which cleanly separates the two peaks.
///
/// This DOES change the classification from the previous fixed mean·0.8 rule: in a flat region
/// (std=0) it coincides with it, but wherever local contrast is present it raises the threshold,
/// so the dark map dilates at edges. That is the intended behavior and the textbook-correct
/// direction for uneven-illumination binarization; it feeds only the camera finder detector, and
/// passes the full simulated-capture suite (perspective, blur, JPEG, vignette, glare, barrel). It
/// has not been validated on physical camera hardware — like the whole camera path, that relies on
/// the simulation. Cost: a second summed-area table (sum of squares) for the local variance, so
/// peak binarizer memory doubles on very high-resolution captures.
/// </summary>
internal sealed class AdaptiveBinarizer : IAdaptiveBinarizer
{
    private const double SauvolaK = 0.2;   // contrast sensitivity; flat regions → mean·(1−k)
    private const double SauvolaR = 128.0; // dynamic range of the local std for an 8-bit image

    public bool[] Threshold(Bitmap photo)
    {
        int w = photo.Width, h = photo.Height;
        var lum = new byte[w * h];
        for (int i = 0; i < lum.Length; i++)
        {
            var p = photo.Px[i];
            lum[i] = (byte)((p.R + p.G + p.B) / 3);
        }

        // Two summed-area tables: luminance and luminance², so a box query gives the local mean
        // and variance (hence std) in O(1) per pixel.
        int stride = w + 1;
        var integral = new long[stride * (h + 1)];
        var integralSq = new long[stride * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0, rowSumSq = 0;
            for (int x = 0; x < w; x++)
            {
                int v = lum[y * w + x];
                rowSum += v;
                rowSumSq += (long)v * v;
                int idx = (y + 1) * stride + x + 1;
                integral[idx] = integral[y * stride + x + 1] + rowSum;
                integralSq[idx] = integralSq[y * stride + x + 1] + rowSumSq;
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
                long sum = Box(integral, stride, x0, y0, x1, y1);
                long sumSq = Box(integralSq, stride, x0, y0, x1, y1);
                long area = (long)(x1 - x0) * (y1 - y0);

                double mean = (double)sum / area;
                double variance = (double)sumSq / area - mean * mean;
                double std = variance > 0 ? Math.Sqrt(variance) : 0;
                double threshold = mean * (1 + SauvolaK * (std / SauvolaR - 1));
                dark[y * w + x] = lum[y * w + x] < threshold;
            }
        }
        return dark;
    }

    private static long Box(long[] table, int stride, int x0, int y0, int x1, int y1) =>
        table[y1 * stride + x1] - table[y0 * stride + x1] - table[y1 * stride + x0] + table[y0 * stride + x0];
}
