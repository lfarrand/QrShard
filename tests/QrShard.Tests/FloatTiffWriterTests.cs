using System.Buffers.Binary;

namespace QrShard.Tests;

public class FloatTiffWriterTests
{
    [Fact]
    public void Write_TwoByTwoScRgb_WritesLittleEndianIeeeFloatTiff()
    {
        float[] pixels =
        [
            1f, 0f, 0f, 1f,
            0f, 1f, 0f, 1f,
            0f, 0f, 1f, 1f,
            1f, 1f, 1f, 1f,
        ];
        using var stream = new MemoryStream();

        Recorder.FloatTiffWriter.Write(stream, pixels, width: 2, height: 2);

        byte[] tiff = stream.ToArray();
        Assert.True(tiff.Length > 8);
        Assert.Equal((byte)'I', tiff[0]);
        Assert.Equal((byte)'I', tiff[1]);
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)));
        Assert.Equal(11, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(8)));
    }

    [Fact]
    public void ConvertPq10ToScRgb_ZeroCodes_AreBlackWithOpaqueAlpha()
    {
        var buffer = new byte[4];
        var floats = new float[4];

        Recorder.ScRgb.ConvertPq10ToScRgb(buffer, floats, pixelCount: 1);

        Assert.Equal(0f, floats[0]);
        Assert.Equal(0f, floats[1]);
        Assert.Equal(0f, floats[2]);
        Assert.Equal(1f, floats[3]);
    }
}
