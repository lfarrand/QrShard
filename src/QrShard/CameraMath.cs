namespace QrShard;

/// <summary>Small numeric helpers shared across the camera-rectification stages.</summary>
internal sealed class CameraMath
{
    public double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public double Lerp(double a, double b, double t) => a + (b - a) * t;
}
