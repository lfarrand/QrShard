using QrShard;

namespace QrShard.Tests;

public class MonitorResolutionTests
{
    [Fact]
    public void Xrandr_PrimaryOutput_IsPreferred()
    {
        const string output =
            """
            Screen 0: minimum 320 x 200, current 5760 x 2160, maximum 16384 x 16384
            HDMI-1 connected 1920x1080+3840+0 (normal left inverted right x axis y axis) 527mm x 296mm
               1920x1080     60.00*+
            DP-1 connected primary 3840x2160+0+0 (normal left inverted right x axis y axis) 600mm x 340mm
               3840x2160     60.00*+  30.00
               1920x1080     60.00
            HDMI-2 disconnected (normal left inverted right x axis y axis)
            """;
        Assert.Equal((3840, 2160), MonitorResolution.TryParseXrandr(output));
    }

    [Fact]
    public void Xrandr_NoPrimaryMarker_UsesFirstConnectedOutput()
    {
        const string output =
            """
            Screen 0: minimum 320 x 200, current 2560 x 1440, maximum 8192 x 8192
            eDP-1 connected 2560x1440+0+0 (normal left inverted right x axis y axis) 309mm x 174mm
               2560x1440     165.00*+  60.00
            """;
        Assert.Equal((2560, 1440), MonitorResolution.TryParseXrandr(output));
    }

    [Fact]
    public void Xrandr_ConnectedWithoutGeometry_FallsBackToActiveModeLine()
    {
        const string output =
            """
            Screen 0: minimum 320 x 200, current 1920 x 1200, maximum 8192 x 8192
            Virtual-1 connected (normal left inverted right x axis y axis)
               1920x1200     59.95*+
               1600x1200     59.95
            """;
        Assert.Equal((1920, 1200), MonitorResolution.TryParseXrandr(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Can't open display")]
    [InlineData("HDMI-1 disconnected (normal left inverted right x axis y axis)")]
    public void Xrandr_UndetectableOutput_ReturnsNull(string output) =>
        Assert.Null(MonitorResolution.TryParseXrandr(output));

    [Fact]
    public void Xrandr_ModeListWithoutStar_ReturnsNull()
    {
        // Connected but inactive output: modes listed, none current.
        const string output =
            """
            DP-2 connected (normal left inverted right x axis y axis)
               1920x1080     60.00 +
               1280x720      60.00
            """;
        Assert.Null(MonitorResolution.TryParseXrandr(output));
    }
}
