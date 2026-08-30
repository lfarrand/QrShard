namespace QrShard.Tests;

public class TerminationMatchTests
{
    private static readonly Recorder.Rgb Black = new(0, 0, 0);
    private static readonly Recorder.Rgb Gray = new(64, 64, 64);

    [Fact]
    public void IsUniformBgra8_ExactBlack_MatchesToleranceZero()
    {
        byte[] buffer = FillBgra(64, 0, 0, 0);
        Assert.True(Recorder.TerminationMatch.IsUniformBgra8(buffer, 64, Black, tolerance: 0));
    }

    [Fact]
    public void IsUniformBgra8_OneDifferentPixel_IsNotUniform()
    {
        byte[] buffer = FillBgra(64, 0, 0, 0);
        buffer[20] = 40;
        Assert.False(Recorder.TerminationMatch.IsUniformBgra8(buffer, 64, Black, tolerance: 30));
    }

    [Fact]
    public void IsUniformBgra8_UniformShiftWithinTolerance_Matches()
    {
        byte[] buffer = FillBgra(64, 39, 39, 39);
        Assert.True(Recorder.TerminationMatch.IsUniformBgra8(buffer, 64, Gray, tolerance: 30));
    }

    [Fact]
    public void IsUniformBgra8_UniformShiftOutsideTolerance_DoesNotMatch()
    {
        byte[] buffer = FillBgra(64, 0, 0, 0);
        Assert.False(Recorder.TerminationMatch.IsUniformBgra8(buffer, 64, Gray, tolerance: 30));
    }

    [Fact]
    public void ParseTolerance_OutOfRange_ClampsToByte()
    {
        Assert.Equal(0, Recorder.SettingParsers.ParseTolerance("-4"));
        Assert.Equal(255, Recorder.SettingParsers.ParseTolerance("999"));
        Assert.Equal(30, Recorder.SettingParsers.ParseTolerance("nope"));
    }

    [Fact]
    public void ParseBool_MissingOrJunk_IsFalse()
    {
        Assert.False(Recorder.SettingParsers.ParseBool(null));
        Assert.False(Recorder.SettingParsers.ParseBool("yes"));
        Assert.True(Recorder.SettingParsers.ParseBool("true"));
    }

    [Fact]
    public void ParseScreenIndex_NegativeOrJunk_IsPrimary()
    {
        Assert.Equal(0, Recorder.SettingParsers.ParseScreenIndex("-1"));
        Assert.Equal(0, Recorder.SettingParsers.ParseScreenIndex("x"));
        Assert.Equal(2, Recorder.SettingParsers.ParseScreenIndex("2"));
    }

    [Fact]
    public void IsUniformFp16_SolidBlack_Matches()
    {
        byte[] buffer = FillFp16(16, 0f, 0f, 0f);
        Assert.True(Recorder.TerminationMatch.IsUniformFp16(buffer, 16, Black, tolerance: 0));
    }

    [Fact]
    public void IsUniformFp16_OneHotPixel_IsNotUniform()
    {
        byte[] buffer = FillFp16(16, 0f, 0f, 0f);
        Span<Half> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(buffer.AsSpan());
        pixels[8] = (Half)1f;
        Assert.False(Recorder.TerminationMatch.IsUniformFp16(buffer, 16, Black, tolerance: 30));
    }

    [Fact]
    public void IsUniformPq10_SolidZeroCodes_MatchesBlack()
    {
        byte[] buffer = new byte[16 * 4];
        Assert.True(Recorder.TerminationMatch.IsUniformPq10(buffer, 16, Black, tolerance: 0));
    }

    [Fact]
    public void ToSrgbByte_SdrWhite_Is255()
    {
        Assert.Equal(255, Recorder.ScRgb.ToSrgbByte(1f));
        Assert.Equal(0, Recorder.ScRgb.ToSrgbByte(0f));
    }

    private static byte[] FillBgra(int pixels, byte b, byte g, byte r)
    {
        var buffer = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++)
        {
            buffer[i * 4] = b;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = r;
            buffer[i * 4 + 3] = 255;
        }
        return buffer;
    }

    private static byte[] FillFp16(int pixels, float r, float g, float b)
    {
        var buffer = new byte[pixels * 8];
        Span<Half> pixels16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(buffer.AsSpan());
        for (int i = 0; i < pixels; i++)
        {
            pixels16[i * 4] = (Half)r;
            pixels16[i * 4 + 1] = (Half)g;
            pixels16[i * 4 + 2] = (Half)b;
            pixels16[i * 4 + 3] = (Half)1f;
        }
        return buffer;
    }
}
