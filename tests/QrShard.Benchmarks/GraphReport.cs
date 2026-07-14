using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Reports;

namespace QrShard.Benchmarks;

/// <summary>
/// Turns BenchmarkDotNet summaries into a self-contained HTML report with SVG charts:
/// codec time vs file size, estimated end-to-end transfer time (including screenshot-capture
/// cadence), codec throughput, and the full numbers table. Written next to the standard
/// BenchmarkDotNet artifacts, so it complements the CSV/Markdown/R-plot exporters.
/// </summary>
internal static class GraphReport
{
    private sealed record Row(string SizeLabel, long SizeBytes, string Preset)
    {
        public double EncodeSec { get; set; } = double.NaN;
        public double DecodeSec { get; set; } = double.NaN;
        public bool Complete => !double.IsNaN(EncodeSec) && !double.IsNaN(DecodeSec);
        public double CodecSec => EncodeSec + DecodeSec;
    }

    private sealed record Series(string Name, int Slot, bool Dashed, List<(double X, double Y)> Points);

    public static void Write(IReadOnlyList<Summary> summaries, TextWriter log)
    {
        var rows = Collect(summaries);
        if (rows.Count == 0)
        {
            log.WriteLine("GraphReport: no successful benchmark results to plot.");
            return;
        }
        WriteCore(rows, summaries[0].ResultsDirectoryPath, log);
    }

    /// <summary>Regenerates the graphs from previously persisted results, without running benchmarks.</summary>
    public static void WriteFromPersisted(string resultsDir, TextWriter log) => WriteCore([], resultsDir, log);

    private static void WriteCore(List<Row> rows, string dir, TextWriter log)
    {
        Directory.CreateDirectory(dir);

        // Merge with results persisted by earlier runs, so the matrix can be benchmarked in
        // several sittings (e.g. small sizes now, the 1 GB cases overnight) and the graphs
        // always reflect everything measured so far. Latest measurement of a case wins.
        rows = MergeWithPersisted(rows, Path.Combine(dir, "transfer-results.json"));
        if (rows.Count == 0)
        {
            log.WriteLine("GraphReport: no persisted results found to plot.");
            return;
        }

        string path = Path.Combine(dir, "transfer-graphs.html");
        File.WriteAllText(path, Render(rows));
        log.WriteLine();
        log.WriteLine($"// * Transfer graphs: {path} ({rows.Count} case(s), merged across runs) *");
    }

    private sealed record PersistedRow(string Size, string Preset, double EncodeSec, double DecodeSec);

    private static List<Row> MergeWithPersisted(List<Row> fresh, string jsonPath)
    {
        var merged = new Dictionary<(string, string), Row>();
        if (File.Exists(jsonPath))
        {
            var persisted = JsonSerializer.Deserialize<List<PersistedRow>>(File.ReadAllText(jsonPath)) ?? [];
            foreach (var p in persisted)
            {
                merged[(p.Size, p.Preset)] = new Row(p.Size, BenchPresets.ParseSize(p.Size), p.Preset)
                {
                    EncodeSec = p.EncodeSec < 0 ? double.NaN : p.EncodeSec,
                    DecodeSec = p.DecodeSec < 0 ? double.NaN : p.DecodeSec,
                };
            }
        }
        foreach (var r in fresh)
        {
            if (merged.TryGetValue((r.SizeLabel, r.Preset), out var old))
            {
                if (double.IsNaN(r.EncodeSec))
                    r.EncodeSec = old.EncodeSec;
                if (double.IsNaN(r.DecodeSec))
                    r.DecodeSec = old.DecodeSec;
            }
            merged[(r.SizeLabel, r.Preset)] = r;
        }

        var rows = merged.Values.OrderBy(r => r.SizeBytes).ThenBy(r => PresetSlot(r.Preset)).ToList();
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(
            rows.Select(r => new PersistedRow(r.SizeLabel, r.Preset,
                double.IsNaN(r.EncodeSec) ? -1 : r.EncodeSec,
                double.IsNaN(r.DecodeSec) ? -1 : r.DecodeSec)),
            new JsonSerializerOptions { WriteIndented = true }));
        return rows;
    }

    private static List<Row> Collect(IReadOnlyList<Summary> summaries)
    {
        var rows = new Dictionary<(string, string), Row>();
        foreach (var summary in summaries)
        {
            foreach (var report in summary.Reports)
            {
                if (report.ResultStatistics is null)
                    continue;
                var bc = report.BenchmarkCase;
                string size = bc.Parameters["FileSize"]?.ToString() ?? "";
                string preset = bc.Parameters["Preset"]?.ToString() ?? "";
                if (size.Length == 0 || preset.Length == 0)
                    continue;

                var row = rows.TryGetValue((size, preset), out var existing)
                    ? existing
                    : rows[(size, preset)] = new Row(size, BenchPresets.ParseSize(size), preset);
                double seconds = report.ResultStatistics.Mean / 1e9;
                switch (bc.Descriptor.WorkloadMethod.Name)
                {
                    case nameof(TransferBenchmarks.Encode):
                        row.EncodeSec = seconds;
                        break;
                    case nameof(TransferBenchmarks.Decode):
                        row.DecodeSec = seconds;
                        break;
                }
            }
        }
        return [.. rows.Values.OrderBy(r => r.SizeBytes).ThenBy(r => PresetSlot(r.Preset))];
    }

    private static int PresetSlot(string preset)
    {
        int i = BenchPresets.DefaultPresets.Split(',').ToList().IndexOf(preset);
        return i >= 0 ? i : 3; // unknown presets share the last slot
    }

    // ---------- HTML assembly ----------

    private static string Render(List<Row> rows)
    {
        var presets = rows.Select(r => r.Preset).Distinct().OrderBy(PresetSlot).ToList();
        var sb = new StringBuilder();
        sb.Append(Header());

        // Chart 1: codec time (encode solid, decode dashed) vs size, log-log.
        var codec = new List<Series>();
        foreach (string p in presets)
        {
            codec.Add(new Series($"{p} encode", PresetSlot(p), false,
                Points(rows, p, r => r.EncodeSec)));
            codec.Add(new Series($"{p} decode", PresetSlot(p), true,
                Points(rows, p, r => r.DecodeSec)));
        }
        sb.Append(Section("Codec time by file size",
            "Mean wall-clock for turning a file into shard PNGs on disk (encode, solid) and PNGs back into a verified file (decode, dashed). Log-log.",
            codec, rows));

        // Chart 2: estimated end-to-end transfer including capture cadence.
        var manual = presets.Select(p => new Series(p, PresetSlot(p), false,
            Points(rows, p, r => EstimatedTransferSec(r, 3.0)))).ToList();
        var auto = presets.Select(p => new Series(p, PresetSlot(p), false,
            Points(rows, p, r => EstimatedTransferSec(r, 0.5)))).ToList();
        sb.Append(Section("Estimated end-to-end transfer, manual capture (3 s/image)",
            "Codec time plus screenshot cadence: encode + decode + images x 3 s. This is the realistic hand-driven number.",
            manual, rows));
        sb.Append(Section("Estimated end-to-end transfer, automated capture (0.5 s/image)",
            "Same, with a scripted display-and-screenshot loop at 0.5 s per image.",
            auto, rows));

        // Chart 3: codec round-trip throughput.
        var throughput = presets.Select(p => new Series(p, PresetSlot(p), false,
            Points(rows, p, r => r.Complete ? r.SizeBytes / 1048576.0 / r.CodecSec : double.NaN))).ToList();
        sb.Append(Section("Codec round-trip throughput",
            "MB of payload processed per second of codec time (encode + decode, capture excluded).",
            throughput, rows, yFormatter: v => $"{Compact(v)} MB/s"));

        sb.Append(Table(rows));
        sb.Append("</main>");
        return sb.ToString();
    }

    private static List<(double, double)> Points(List<Row> rows, string preset, Func<Row, double> y) =>
        rows.Where(r => r.Preset == preset)
            .Select(r => ((double)r.SizeBytes, y(r)))
            .Where(p => !double.IsNaN(p.Item2) && p.Item2 > 0)
            .OrderBy(p => p.Item1)
            .ToList();

    private static double EstimatedTransferSec(Row r, double secondsPerImage)
    {
        if (!r.Complete)
            return double.NaN;
        var (data, parity) = BenchPresets.EstimateImages(r.Preset, r.SizeBytes);
        return r.CodecSec + (data + parity) * secondsPerImage;
    }

    private static string Section(string title, string caption, List<Series> series, List<Row> rows,
        Func<double, string>? yFormatter = null)
    {
        var sb = new StringBuilder();
        sb.Append($"<section><h2>{title}</h2><p class=\"caption\">{caption}</p>");
        sb.Append("<div class=\"legend\">");
        foreach (var s in series)
        {
            string dash = s.Dashed ? " stroke-dasharray=\"6 4\"" : "";
            sb.Append($"<span class=\"key\"><svg width=\"22\" height=\"10\"><line x1=\"1\" y1=\"5\" x2=\"21\" y2=\"5\" class=\"s{s.Slot}\"{dash} stroke-width=\"2.5\"/></svg>{s.Name}</span>");
        }
        sb.Append("</div>");
        sb.Append(LineChart(series, rows, yFormatter ?? FormatSeconds));
        sb.Append("</section>");
        return sb.ToString();
    }

    // ---------- SVG log-log line chart ----------

    private static string LineChart(List<Series> series, List<Row> rows, Func<double, string> yFormat)
    {
        const int W = 680, H = 380, L = 76, R = 18, T = 14, B = 46;
        double plotW = W - L - R, plotH = H - T - B;

        var allPoints = series.SelectMany(s => s.Points).ToList();
        if (allPoints.Count == 0)
            return "<p class=\"caption\">(no data)</p>";

        double xMin = allPoints.Min(p => p.Item1), xMax = allPoints.Max(p => p.Item1);
        double yMin = allPoints.Min(p => p.Item2), yMax = allPoints.Max(p => p.Item2);
        double lyMin = Math.Floor(Math.Log10(yMin)), lyMax = Math.Ceiling(Math.Log10(yMax));
        if (lyMax - lyMin < 1)
            lyMax = lyMin + 1;
        double lxMin = Math.Log10(xMin), lxMax = Math.Log10(xMax);
        if (lxMax - lxMin < 0.01)
        {
            lxMin -= 0.5;
            lxMax += 0.5;
        }

        double X(double v) => L + (Math.Log10(v) - lxMin) / (lxMax - lxMin) * plotW;
        double Y(double v) => T + plotH - (Math.Log10(v) - lyMin) / (lyMax - lyMin) * plotH;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\">");

        // Y gridlines + labels at decades.
        for (double e = lyMin; e <= lyMax + 0.001; e += 1)
        {
            double y = Y(Math.Pow(10, e));
            sb.Append($"<line x1=\"{L}\" y1=\"{F(y)}\" x2=\"{W - R}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{L - 8}\" y=\"{F(y + 3.5)}\" class=\"tick\" text-anchor=\"end\">{yFormat(Math.Pow(10, e))}</text>");
        }

        // X gridlines + labels at the benchmarked sizes.
        foreach (var (label, bytes) in rows.Select(r => (r.SizeLabel, r.SizeBytes)).Distinct().OrderBy(t => t.SizeBytes))
        {
            double x = X(bytes);
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{T}\" x2=\"{F(x)}\" y2=\"{T + plotH}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{F(x)}\" y=\"{H - B + 18}\" class=\"tick\" text-anchor=\"middle\">{label}</text>");
        }

        // Baseline.
        sb.Append($"<line x1=\"{L}\" y1=\"{T + plotH}\" x2=\"{W - R}\" y2=\"{T + plotH}\" class=\"axis\"/>");

        // Series lines and hoverable markers.
        foreach (var s in series)
        {
            if (s.Points.Count == 0)
                continue;
            string dash = s.Dashed ? " stroke-dasharray=\"6 4\"" : "";
            string poly = string.Join(" ", s.Points.Select(p => $"{F(X(p.Item1))},{F(Y(p.Item2))}"));
            sb.Append($"<polyline points=\"{poly}\" class=\"s{s.Slot} line\"{dash}/>");
            foreach (var (x, y) in s.Points)
            {
                string sizeLabel = rows.First(r => r.SizeBytes == (long)x).SizeLabel;
                sb.Append($"<circle cx=\"{F(X(x))}\" cy=\"{F(Y(y))}\" r=\"3.5\" class=\"s{s.Slot} dot\">" +
                          $"<title>{s.Name} at {sizeLabel}: {yFormat(y)}</title></circle>");
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatSeconds(double s) => s switch
    {
        < 0.001 => $"{s * 1e6:0.#} us",
        < 1 => $"{s * 1e3:0.#} ms",
        < 60 => $"{s:0.##} s",
        < 3600 => $"{s / 60:0.#} min",
        _ => $"{s / 3600:0.##} h",
    };

    private static string Compact(double v) =>
        v >= 100 ? $"{v:0}" : v >= 1 ? $"{v:0.#}" : v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------- Numbers table ----------

    private static string Table(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<section><h2>All measurements</h2><div class=\"scroll\"><table><thead><tr>" +
                  "<th>Size</th><th>Preset</th><th>Images</th><th>Encode</th><th>Decode</th>" +
                  "<th>Codec MB/s</th><th>Est. manual (3 s/img)</th><th>Est. auto (0.5 s/img)</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            var (data, parity) = BenchPresets.EstimateImages(r.Preset, r.SizeBytes);
            string images = parity > 0 ? $"{data}+{parity}p" : data.ToString();
            string mbps = r.Complete ? Compact(r.SizeBytes / 1048576.0 / r.CodecSec) : "&mdash;";
            sb.Append($"<tr><td>{r.SizeLabel}</td><td>{r.Preset}</td><td>{images}</td>" +
                      $"<td>{Cell(r.EncodeSec)}</td><td>{Cell(r.DecodeSec)}</td><td>{mbps}</td>" +
                      $"<td>{Cell(EstimatedTransferSec(r, 3.0))}</td><td>{Cell(EstimatedTransferSec(r, 0.5))}</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
        return sb.ToString();

        static string Cell(double seconds) => double.IsNaN(seconds) ? "—" : FormatSeconds(seconds);
    }

    private static string Header() =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>QrShard transfer benchmarks</title>
        <style>
          :root { color-scheme: light dark; }
          .viz {
            --surface: #fcfcfb; --ink: #0b0b0b; --ink-2: #52514e; --muted: #898781;
            --grid: #e1e0d9; --axis: #c3c2b7;
            --c0: #2a78d6; --c1: #1baf7a; --c2: #eda100; --c3: #008300;
          }
          @media (prefers-color-scheme: dark) {
            .viz {
              --surface: #1a1a19; --ink: #ffffff; --ink-2: #c3c2b7; --muted: #898781;
              --grid: #2c2c2a; --axis: #383835;
              --c0: #3987e5; --c1: #199e70; --c2: #c98500; --c3: #008300;
            }
          }
          body { margin: 0; background: var(--surface); }
          main { max-width: 760px; margin: 0 auto; padding: 24px 16px 64px;
                 font-family: system-ui, -apple-system, "Segoe UI", sans-serif; color: var(--ink); }
          h1 { font-size: 22px; margin: 0 0 4px; }
          h2 { font-size: 16px; margin: 36px 0 4px; }
          .caption, .meta { color: var(--ink-2); font-size: 13px; margin: 2px 0 10px; }
          .legend { display: flex; flex-wrap: wrap; gap: 4px 16px; margin: 6px 0 8px; font-size: 12px; color: var(--ink-2); }
          .key { display: inline-flex; align-items: center; gap: 6px; }
          svg { width: 100%; height: auto; display: block; }
          .grid { stroke: var(--grid); stroke-width: 1; }
          .axis { stroke: var(--axis); stroke-width: 1; }
          .tick { fill: var(--muted); font-size: 10.5px; font-family: system-ui, sans-serif; }
          .line { fill: none; stroke-width: 2; }
          .dot { stroke: var(--surface); stroke-width: 2; }
          .s0 { stroke: var(--c0); } .s1 { stroke: var(--c1); } .s2 { stroke: var(--c2); } .s3 { stroke: var(--c3); }
          circle.s0 { fill: var(--c0); } circle.s1 { fill: var(--c1); } circle.s2 { fill: var(--c2); } circle.s3 { fill: var(--c3); }
          .scroll { overflow-x: auto; }
          table { border-collapse: collapse; font-size: 13px; width: 100%; }
          th, td { text-align: right; padding: 5px 10px; border-bottom: 1px solid var(--grid); font-variant-numeric: tabular-nums; }
          th:nth-child(-n+2), td:nth-child(-n+2) { text-align: left; }
          th { color: var(--ink-2); font-weight: 600; }
        </style></head><body class="viz"><main>
        <h1>QrShard transfer benchmarks</h1>
        <p class="meta">Generated {{DateTime.Now:yyyy-MM-dd HH:mm}} &middot; {{Environment.ProcessorCount}} logical cores &middot; {{Environment.OSVersion}}</p>
        """;
}
