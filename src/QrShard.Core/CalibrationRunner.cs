namespace QrShard;

/// <summary>
/// Removes the capture-density guesswork: <see cref="Generate"/> writes a ladder of probe
/// shards at increasing density; the user displays and captures them exactly as they would a
/// real transfer, and <see cref="Analyze"/> measures what actually survived — recommending the
/// densest settings that decoded with comfortable ECC headroom on THIS screen/capture pair.
/// </summary>
internal sealed class CalibrationRunner(IShardEncoder encoder, IShardDecoder decoder) : ICalibration
{
    public CalibrationRunner() : this(new ShardEncoder(), new ShardDecoder())
    {
    }

    /// <summary>Densest first, so the analysis report reads top-down from ambitious to safe.</summary>
    private static readonly (int CellPx, int Bits)[] ScreenProbes =
        [(1, 8), (1, 6), (2, 6), (2, 4), (3, 4), (4, 4), (8, 2)];

    /// <summary>Camera-profile ladder: photo capture needs several camera pixels per cell.</summary>
    private static readonly (int CellPx, int Bits)[] CameraProbes =
        [(4, 2), (5, 2), (6, 2), (8, 2), (10, 2)];

    private const int ScreenEccParity = 16;
    private const int CameraEccParity = 32;

    public int Generate(string outDir, int width, int height, bool camera, TextWriter output)
    {
        var probes = camera ? CameraProbes : ScreenProbes;
        int eccParity = camera ? CameraEccParity : ScreenEccParity;
        Directory.CreateDirectory(outDir);
        output.WriteLine($"Writing {probes.Length} {(camera ? "camera-profile " : "")}calibration probes ({width}x{height}) → {outDir}");
        foreach (var (cellPx, bits) in probes)
        {
            var layout = Layout.Create(width, height, cellPx, bits, eccParity, camera);
            int payloadSize = (int)Math.Max(1, (layout.UsableBytes - ShardHeader.Size("p.bin")) * 9 / 10);
            string input = Path.Combine(outDir, $"cal-c{cellPx}b{bits}.bin");
            byte[] payload = new byte[payloadSize];
            new Random(cellPx * 100 + bits).NextBytes(payload);
            File.WriteAllBytes(input, payload);
            try
            {
                encoder.Encode(input, outDir, new EncodeOptions
                {
                    Width = width,
                    Height = height,
                    CellPx = cellPx,
                    BitsPerCell = bits,
                    EccParity = eccParity,
                    CameraMode = camera,
                    Compress = false,
                });
            }
            finally
            {
                File.Delete(input);
            }
            output.WriteLine($"  probe: cell {cellPx}px, {bits} bits/cell ({layout.UsableBytes:N0} B capacity)");
        }
        output.WriteLine();
        output.WriteLine("Next: display each probe image full-screen at 100% zoom, capture it the way you");
        output.WriteLine("will capture real transfers (screenshot tool, phone photo, etc.), put the");
        output.WriteLine("captures in one folder, and run: qrshard calibrate <that folder>");
        if (camera)
            output.WriteLine("Remember to add --camera to your real encodes alongside the recommended -c/-b.");
        return 0;
    }

    public int Analyze(string capturedFolder, TextWriter output)
    {
        var images = Directory.EnumerateFiles(capturedFolder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".bmp" or ".jpg" or ".jpeg" or ".webp" or ".tif" or ".tiff")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (images.Count == 0)
        {
            output.WriteLine("No captured probe images found in the folder.");
            return 1;
        }

        // Probes are self-describing: the metadata strip tells us which settings a capture is,
        // so file names don't matter. Keep the best (least-corrected) capture per settings.
        var results = new Dictionary<(int CellPx, int Bits), (bool Ok, double Utilization, string Detail)>();
        foreach (string image in images)
        {
            var diag = decoder.Diagnose(image);
            if (diag.Layout is null)
                continue;
            var key = (diag.Layout.CellPx, diag.Layout.BitsPerCell);
            double correctable = Math.Max(1, diag.Layout.CodewordCount * (diag.Layout.EccParity / 2.0));
            if (diag.Shard is not null)
            {
                double utilization = diag.Shard.CorrectedBytes / correctable;
                string detail = $"decoded, ECC corrected {diag.Shard.CorrectedBytes} bytes ({utilization:P0} of capacity)";
                if (!results.TryGetValue(key, out var existing) || !existing.Ok || utilization < existing.Utilization)
                    results[key] = (true, utilization, detail);
            }
            else if (!results.ContainsKey(key))
            {
                results[key] = (false, 1, "FAILED to decode");
            }
        }
        if (results.Count == 0)
        {
            output.WriteLine("None of the captures contained a locatable probe. Recapture with the full frame visible.");
            return 1;
        }

        output.WriteLine("Probe results (densest first):");
        (int CellPx, int Bits)? recommended = null;
        foreach (var (cellPx, bits) in ScreenProbes.Concat(CameraProbes))
        {
            if (!results.TryGetValue((cellPx, bits), out var r))
                continue;
            output.WriteLine($"  cell {cellPx}px, {bits} bits: {r.Detail}");
            // Recommend the densest probe that decoded using less than half its ECC budget —
            // real transfers should keep headroom for worse captures than the calibration one.
            if (recommended is null && r.Ok && r.Utilization < 0.5)
                recommended = (cellPx, bits);
        }

        if (recommended is var (c, b) && recommended is not null)
        {
            output.WriteLine();
            output.WriteLine($"Recommended encode settings for this setup: -c {c} -b {b}");
            return 0;
        }
        output.WriteLine();
        output.WriteLine("No probe decoded with comfortable headroom. Use the camera profile (--camera) or improve the capture (closer, sharper, less glare).");
        return 1;
    }
}
