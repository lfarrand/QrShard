namespace QrShard;

/// <summary>Integer half-open pixel rectangle (a located frame's outer bounding box).</summary>
internal readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
{
    public int W => X1 - X0;
    public int H => Y1 - Y0;
}

/// <summary>Subpixel-accurate inner rectangle (the white area enclosed by the frame).</summary>
internal readonly record struct InnerRect(double X0, double Y0, double X1, double Y1)
{
    public double W => X1 - X0;
    public double H => Y1 - Y0;
}
