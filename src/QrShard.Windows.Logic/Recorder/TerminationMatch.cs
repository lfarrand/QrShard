using System.Numerics;
using System.Runtime.InteropServices;

namespace QrShard.Recorder;

/// <summary>Detects a uniform full-screen termination colour on captured frames.</summary>
internal static class TerminationMatch
{
    public static bool IsUniformBgra8(byte[] buffer, int pixelCount, Rgb termination, int tolerance)
    {
        if (tolerance == 0)
        {
            uint rgb = ((uint)termination.R << 16) | ((uint)termination.G << 8) | termination.B;
            var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
            return pixels.IndexOfAnyExcept(0xFF000000u | rgb, rgb) < 0;
        }

        byte b0 = buffer[0], g0 = buffer[1], r0 = buffer[2];
        if (ExceedsTolerance(r0, g0, b0, termination, tolerance))
            return false;

        const int UniformEpsilon = 2;
        byte bLow = (byte)Math.Max(b0 - UniformEpsilon, 0);
        byte bHigh = (byte)Math.Min(b0 + UniformEpsilon, 255);
        byte gLow = (byte)Math.Max(g0 - UniformEpsilon, 0);
        byte gHigh = (byte)Math.Min(g0 + UniformEpsilon, 255);
        byte rLow = (byte)Math.Max(r0 - UniformEpsilon, 0);
        byte rHigh = (byte)Math.Min(r0 + UniformEpsilon, 255);

        bool OutOfRange(ReadOnlySpan<byte> p, int i) =>
            p[i] < bLow || p[i] > bHigh ||
            p[i + 1] < gLow || p[i + 1] > gHigh ||
            p[i + 2] < rLow || p[i + 2] > rHigh;

        int sampleStep = Math.Max(1, pixelCount / 1024) * 4;
        for (int s = 0; s < pixelCount * 4; s += sampleStep)
        {
            if (OutOfRange(buffer, s))
                return false;
        }

        int lanes = Vector<byte>.Count;
        Span<byte> lowPattern = stackalloc byte[lanes];
        Span<byte> highPattern = stackalloc byte[lanes];
        for (int lane = 0; lane < lanes; lane++)
        {
            (lowPattern[lane], highPattern[lane]) = (lane % 4) switch
            {
                0 => (bLow, bHigh),
                1 => (gLow, gHigh),
                2 => (rLow, rHigh),
                _ => ((byte)0, (byte)255),
            };
        }
        var low = new Vector<byte>(lowPattern);
        var high = new Vector<byte>(highPattern);

        var span = buffer.AsSpan(0, pixelCount * 4);
        int i = 0;
        for (; i <= span.Length - lanes; i += lanes)
        {
            var v = new Vector<byte>(span.Slice(i, lanes));
            if (!Vector.EqualsAll(Vector.Max(v, low), v) || !Vector.EqualsAll(Vector.Min(v, high), v))
                return false;
        }
        for (; i < span.Length; i += 4)
        {
            if (OutOfRange(span, i))
                return false;
        }
        return true;
    }

    public static bool IsUniformFp16(byte[] buffer, int pixelCount, Rgb termination, int tolerance)
    {
        var pixels = MemoryMarshal.Cast<byte, Half>(buffer.AsSpan(0, pixelCount * 8));
        float r0 = (float)pixels[0];
        float g0 = (float)pixels[1];
        float b0 = (float)pixels[2];

        if (ExceedsTolerance(ScRgb.ToSrgbByte(r0), ScRgb.ToSrgbByte(g0), ScRgb.ToSrgbByte(b0),
                termination, tolerance))
        {
            return false;
        }

        const float UniformEpsilon = 0.005f;
        int sampleStep = Math.Max(1, pixelCount / 1024) * 4;
        for (int s = 0; s < pixels.Length; s += sampleStep)
        {
            if (Math.Abs((float)pixels[s] - r0) > UniformEpsilon ||
                Math.Abs((float)pixels[s + 1] - g0) > UniformEpsilon ||
                Math.Abs((float)pixels[s + 2] - b0) > UniformEpsilon)
            {
                return false;
            }
        }

        for (int i = 4; i < pixels.Length; i += 4)
        {
            if (Math.Abs((float)pixels[i] - r0) > UniformEpsilon ||
                Math.Abs((float)pixels[i + 1] - g0) > UniformEpsilon ||
                Math.Abs((float)pixels[i + 2] - b0) > UniformEpsilon)
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsUniformPq10(byte[] buffer, int pixelCount, Rgb termination, int tolerance)
    {
        float[] lut = ScRgb.PqLut;
        var pixels = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, pixelCount * 4));
        uint first = pixels[0];
        int r0 = (int)(first & 0x3FF);
        int g0 = (int)((first >> 10) & 0x3FF);
        int b0 = (int)((first >> 20) & 0x3FF);

        var (scR, scG, scB) = ScRgb.Bt2020ToScRgb(lut[r0], lut[g0], lut[b0]);
        if (ExceedsTolerance(ScRgb.ToSrgbByte(scR), ScRgb.ToSrgbByte(scG), ScRgb.ToSrgbByte(scB),
                termination, tolerance))
        {
            return false;
        }

        const int UniformEpsilon = 2;
        bool Matches(uint v) =>
            Math.Abs((int)(v & 0x3FF) - r0) <= UniformEpsilon &&
            Math.Abs((int)((v >> 10) & 0x3FF) - g0) <= UniformEpsilon &&
            Math.Abs((int)((v >> 20) & 0x3FF) - b0) <= UniformEpsilon;

        int sampleStep = Math.Max(1, pixelCount / 1024);
        for (int s = 0; s < pixels.Length; s += sampleStep)
        {
            if (!Matches(pixels[s]))
                return false;
        }

        for (int i = 1; i < pixels.Length; i++)
        {
            if (!Matches(pixels[i]))
                return false;
        }
        return true;
    }

    private static bool ExceedsTolerance(int r, int g, int b, Rgb termination, int tolerance) =>
        Math.Abs(r - termination.R) > tolerance ||
        Math.Abs(g - termination.G) > tolerance ||
        Math.Abs(b - termination.B) > tolerance;
}
