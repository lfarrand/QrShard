using System.Runtime.InteropServices;

namespace QrShard.Recorder;

/// <summary>Converts compositor HDR pixels to linear scRGB floats and 8-bit sRGB.</summary>
internal static class ScRgb
{
    private static readonly Lazy<float[]> PqToScRgb = new(() =>
    {
        const double m1 = 0.1593017578125;
        const double m2 = 78.84375;
        const double c1 = 0.8359375;
        const double c2 = 18.8515625;
        const double c3 = 18.6875;
        var lut = new float[1024];
        for (int code = 0; code < 1024; code++)
        {
            double e = Math.Pow(code / 1023.0, 1.0 / m2);
            double y = Math.Pow(Math.Max(e - c1, 0.0) / (c2 - c3 * e), 1.0 / m1);
            lut[code] = (float)(y * 10000.0 / 80.0);
        }
        return lut;
    });

    internal static float[] PqLut => PqToScRgb.Value;

    public static int ToSrgbByte(float linear)
    {
        double l = Math.Clamp(linear, 0f, 1f);
        double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
        return (int)Math.Round(s * 255.0);
    }

    internal static (float R, float G, float B) Bt2020ToScRgb(float r, float g, float b) =>
        (1.6605f * r - 0.5876f * g - 0.0728f * b,
         -0.1246f * r + 1.1329f * g - 0.0083f * b,
         -0.0182f * r - 0.1006f * g + 1.1187f * b);

    public static void ConvertPq10ToScRgb(byte[] buffer, float[] floats, int pixelCount)
    {
        float[] lut = PqLut;
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
        for (int i = 0; i < pixels.Length; i++)
        {
            uint v = pixels[i];
            int o = i * 4;
            (floats[o], floats[o + 1], floats[o + 2]) =
                Bt2020ToScRgb(lut[v & 0x3FF], lut[(v >> 10) & 0x3FF], lut[(v >> 20) & 0x3FF]);
            floats[o + 3] = 1f;
        }
    }
}
