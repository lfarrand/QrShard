using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Canvas-to-photo mapping plus the canvas dimensions and module scale.</summary>
internal sealed record CanvasGeometry(Homography H, int Width, int Height, double Module);

/// <summary>
/// Camera-capture front-end orchestrator: finds the four QR-style finder patterns that
/// camera-profile shards carry in their top/bottom bands, resolves orientation via the tick
/// mark next to the top-left finder, solves the 4-point homography, and resamples the photo
/// into an axis-aligned canvas that the ordinary decode pipeline can consume.
///
/// Phase 1 (binarize → finder detection → quad selection → homography warp) handles rotation
/// (any angle, including 90/180/270), perspective from off-axis shots, and the mild blur/JPEG
/// artifacts of a real photo.
///
/// Phase 2 refines the pure homography for handheld reality, using the black frame itself as a
/// dense alignment structure: traced-edge residuals feed a Coons-patch correction field (lens
/// distortion, mild screen curvature), and interpolated black/white references normalize every
/// rectified pixel per channel (vignette, glare gradients, white-balance shifts). If refinement
/// cannot lock onto the frame, the phase-1 homography result is used as-is.
/// </summary>
internal sealed class CameraRectifier : ICameraRectifier
{
    private const int MaxCanvasDimension = 12000;

    /// <summary>Rectified bitmap, or null when the image carries no detectable finder patterns.</summary>
    public Bitmap? TryRectify(Bitmap photo)
    {
        bool[] dark = AdaptiveBinarizer.Threshold(photo);
        var clusters = FinderDetector.FindCandidates(photo, dark);
        if (clusters.Count < 4)
            return null;

        var quad = QuadSelector.ChooseQuad(clusters);
        if (quad is null)
            return null;

        var oriented = QuadSelector.ResolveOrientation(photo, dark, quad);
        if (oriented is null)
            return null;

        var geometry = BuildGeometry(oriented);
        var coarse = WarpHomography(photo, geometry);
        return TryRefine(photo, coarse, geometry) ?? coarse;
    }

    private static CanvasGeometry BuildGeometry(OrientedQuad q)
    {
        // Canvas geometry: finder centers at the corners of a (margin-inset) rectangle whose
        // size comes from the photographed edge lengths, so canvas scale ≈ photo scale and no
        // resolution is thrown away. Downstream tolerates unequal x/y scale by design.
        double wc = (CameraMath.Dist(q.Tl, q.Tr) + CameraMath.Dist(q.Bl, q.Br)) / 2;
        double hc = (CameraMath.Dist(q.Tl, q.Bl) + CameraMath.Dist(q.Tr, q.Br)) / 2;
        double margin = 8 * q.Module;

        double scale = Math.Min(1.0, MaxCanvasDimension / Math.Max(wc + 2 * margin, hc + 2 * margin));
        wc *= scale;
        hc *= scale;
        margin *= scale;

        int canvasW = (int)Math.Round(wc + 2 * margin);
        int canvasH = (int)Math.Round(hc + 2 * margin);

        Span<(double X, double Y)> canvasCorners =
        [
            (margin, margin), (margin + wc, margin), (margin + wc, margin + hc), (margin, margin + hc),
        ];
        Span<(double X, double Y)> photoCorners = [q.Tl, q.Tr, q.Br, q.Bl];
        return new CanvasGeometry(Homography.Solve(canvasCorners, photoCorners), canvasW, canvasH, q.Module * scale);
    }

    private static Bitmap WarpHomography(Bitmap photo, CanvasGeometry geometry)
    {
        var px = new Rgb24[geometry.Width * geometry.Height];
        for (int y = 0; y < geometry.Height; y++)
        {
            for (int x = 0; x < geometry.Width; x++)
            {
                var (sx, sy) = geometry.H.Apply(x + 0.5, y + 0.5);
                px[y * geometry.Width + x] = photo.SampleBilinear(sx - 0.5, sy - 0.5);
            }
        }
        return new Bitmap(px, geometry.Width, geometry.Height);
    }

    /// <summary>
    /// Locates the frame in the coarse canvas, traces its four edges in the original photo,
    /// and re-warps with a Coons-patch residual correction plus per-pixel black/white
    /// normalization. Returns null (caller keeps the phase-1 result) when the frame cannot be
    /// traced confidently.
    /// </summary>
    private static Bitmap? TryRefine(Bitmap photo, Bitmap coarse, CanvasGeometry geometry)
    {
        var box = CoarseFrameScanner.FindFrameBox(coarse, geometry.Module);
        if (box is null)
            return null;
        var (bx0, by0, bx1, by1) = box.Value;

        const int n = SideTrace.SamplesPerSide;
        var top = FrameEdgeTracer.TraceSide(photo, geometry, i => (CameraMath.Lerp(bx0, bx1, (i + 0.5) / n), by0), (0, -1));
        var bottom = FrameEdgeTracer.TraceSide(photo, geometry, i => (CameraMath.Lerp(bx0, bx1, (i + 0.5) / n), by1), (0, 1));
        var left = FrameEdgeTracer.TraceSide(photo, geometry, i => (bx0, CameraMath.Lerp(by0, by1, (i + 0.5) / n)), (-1, 0));
        var right = FrameEdgeTracer.TraceSide(photo, geometry, i => (bx1, CameraMath.Lerp(by0, by1, (i + 0.5) / n)), (1, 0));
        if (top is null || bottom is null || left is null || right is null)
            return null;

        var map = new RefinedMap(geometry.H, bx0, by0, bx1, by1, top, bottom, left, right);
        return WarpRefined(photo, map, geometry.Width, geometry.Height);
    }

    private static Bitmap WarpRefined(Bitmap photo, RefinedMap map, int canvasW, int canvasH)
    {
        var px = new Rgb24[canvasW * canvasH];
        for (int y = 0; y < canvasH; y++)
        {
            for (int x = 0; x < canvasW; x++)
            {
                var (sx, sy) = map.Apply(x + 0.5, y + 0.5);
                var sample = photo.SampleBilinear(sx - 0.5, sy - 0.5);
                var (black, white) = map.Illumination(x + 0.5, y + 0.5);
                px[y * canvasW + x] = new Rgb24(
                    Normalize(sample.R, black[0], white[0]),
                    Normalize(sample.G, black[1], white[1]),
                    Normalize(sample.B, black[2], white[2]));
            }
        }
        return new Bitmap(px, canvasW, canvasH);

        // Linear per-channel remap against the locally interpolated black/white references —
        // this is what flattens vignette, glare gradients, and white-balance shifts.
        static byte Normalize(byte value, double black, double white)
        {
            double range = Math.Max(white - black, 24);
            return (byte)Math.Clamp((value - black) * 255.0 / range + 0.5, 0, 255);
        }
    }
}
