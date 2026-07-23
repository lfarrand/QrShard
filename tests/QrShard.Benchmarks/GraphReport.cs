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

    private sealed record Chart(string Slug, string Title, string Caption, List<Series> Series,
        Func<double, string> YFormat);

    /// <summary>
    /// Concrete colors for a standalone chart. The HTML report drives the same values through CSS
    /// custom properties, but a .svg file embedded in Markdown cannot: GitHub's sanitizer drops
    /// &lt;style&gt; blocks, so every attribute has to be inlined and each theme needs its own file.
    /// </summary>
    private sealed record Palette(string Name, string Surface, string Ink, string Ink2, string Muted,
        string Grid, string Axis, string[] Colors)
    {
        public static readonly Palette Light = new("light", "#fcfcfb", "#0b0b0b", "#52514e", "#898781",
            "#e1e0d9", "#c3c2b7", ["#2a78d6", "#1baf7a", "#eda100", "#008300"]);

        public static readonly Palette Dark = new("dark", "#1a1a19", "#ffffff", "#c3c2b7", "#898781",
            "#2c2c2a", "#383835", ["#3987e5", "#199e70", "#c98500", "#008300"]);
    }

    private const string SvgFont = "system-ui, -apple-system, Segoe UI, sans-serif";

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
        var sb = new StringBuilder();
        sb.Append(Header());
        foreach (var chart in Charts(rows))
            sb.Append(Section(chart, rows));
        sb.Append(Table(rows));
        sb.Append("</main>");
        return sb.ToString();
    }

    /// <summary>
    /// The chart set, defined once and rendered by both the HTML report and the standalone
    /// README assets, so the two can never drift apart.
    /// </summary>
    private static List<Chart> Charts(List<Row> rows)
    {
        var presets = rows.Select(r => r.Preset).Distinct().OrderBy(PresetSlot).ToList();

        // Chart 1: codec time (encode solid, decode dashed) vs size, log-log.
        var codec = new List<Series>();
        foreach (string p in presets)
        {
            codec.Add(new Series($"{p} encode", PresetSlot(p), false,
                Points(rows, p, r => r.EncodeSec)));
            codec.Add(new Series($"{p} decode", PresetSlot(p), true,
                Points(rows, p, r => r.DecodeSec)));
        }

        List<Series> PerPreset(Func<Row, double> y) =>
            presets.Select(p => new Series(p, PresetSlot(p), false, Points(rows, p, y))).ToList();

        return
        [
            new Chart("codec-time", "Codec time by file size",
                "Mean wall-clock for turning a file into shard PNGs on disk (encode, solid) and PNGs back into a verified file (decode, dashed). Log-log.",
                codec, FormatSeconds),

            // Charts 2 and 3: estimated end-to-end transfer including capture cadence.
            new Chart("transfer-manual", "Estimated end-to-end transfer, manual capture (3 s/image)",
                "Codec time plus screenshot cadence: encode + decode + images x 3 s. This is the realistic hand-driven number.",
                PerPreset(r => EstimatedTransferSec(r, 3.0)), FormatSeconds),
            new Chart("transfer-auto", "Estimated end-to-end transfer, automated capture (0.5 s/image)",
                "Same, with a scripted display-and-screenshot loop at 0.5 s per image.",
                PerPreset(r => EstimatedTransferSec(r, 0.5)), FormatSeconds),

            // Chart 4: codec round-trip throughput.
            new Chart("throughput", "Codec round-trip throughput",
                "MB of payload processed per second of codec time (encode + decode, capture excluded).",
                PerPreset(r => r.Complete ? r.SizeBytes / 1048576.0 / r.CodecSec : double.NaN),
                v => $"{Compact(v)} MB/s"),
        ];
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

    private static string Section(Chart chart, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append($"<section><h2>{chart.Title}</h2><p class=\"caption\">{chart.Caption}</p>");
        sb.Append("<div class=\"legend\">");
        foreach (var s in chart.Series)
        {
            string dash = s.Dashed ? " stroke-dasharray=\"6 4\"" : "";
            sb.Append($"<span class=\"key\"><svg width=\"22\" height=\"10\"><line x1=\"1\" y1=\"5\" x2=\"21\" y2=\"5\" class=\"s{s.Slot}\"{dash} stroke-width=\"2.5\"/></svg>{s.Name}</span>");
        }
        sb.Append("</div>");
        string body = ChartBody(chart.Series, rows, chart.YFormat, palette: null);
        sb.Append(body.Length == 0
            ? "<p class=\"caption\">(no data)</p>"
            : $"<svg viewBox=\"0 0 {ChartW} {ChartH}\" role=\"img\">{body}</svg>");
        sb.Append("</section>");
        return sb.ToString();
    }

    // ---------- SVG log-log line chart ----------

    private const int ChartW = 680, ChartH = 380;

    /// <summary>
    /// Emits the chart's inner SVG markup (no &lt;svg&gt; wrapper). With <paramref name="palette"/>
    /// null it styles via CSS classes for the HTML report; with a palette it inlines every
    /// presentation attribute so the markup survives as a standalone, sanitizer-proof file.
    /// </summary>
    private static string ChartBody(List<Series> series, List<Row> rows, Func<double, string> yFormat,
        Palette? palette)
    {
        const int W = ChartW, H = ChartH, L = 76, R = 18, T = 14, B = 46;
        double plotW = W - L - R, plotH = H - T - B;

        var allPoints = series.SelectMany(s => s.Points).ToList();
        if (allPoints.Count == 0)
            return "";

        // Class-based styling for the HTML report; fully inlined attributes for standalone files.
        string gridAttr = palette is null ? "class=\"grid\"" : $"stroke=\"{palette.Grid}\" stroke-width=\"1\"";
        string axisAttr = palette is null ? "class=\"axis\"" : $"stroke=\"{palette.Axis}\" stroke-width=\"1\"";
        string tickAttr = palette is null
            ? "class=\"tick\""
            : $"fill=\"{palette.Muted}\" font-size=\"10.5\" font-family=\"{SvgFont}\"";
        string LineAttr(int slot) => palette is null
            ? $"class=\"s{slot} line\""
            : $"fill=\"none\" stroke=\"{palette.Colors[slot % palette.Colors.Length]}\" stroke-width=\"2\"";
        string DotAttr(int slot) => palette is null
            ? $"class=\"s{slot} dot\""
            : $"fill=\"{palette.Colors[slot % palette.Colors.Length]}\" stroke=\"{palette.Surface}\" stroke-width=\"2\"";

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

        // Y gridlines + labels at decades.
        for (double e = lyMin; e <= lyMax + 0.001; e += 1)
        {
            double y = Y(Math.Pow(10, e));
            sb.Append($"<line x1=\"{L}\" y1=\"{F(y)}\" x2=\"{W - R}\" y2=\"{F(y)}\" {gridAttr}/>");
            sb.Append($"<text x=\"{L - 8}\" y=\"{F(y + 3.5)}\" {tickAttr} text-anchor=\"end\">{yFormat(Math.Pow(10, e))}</text>");
        }

        // X gridlines + labels at the benchmarked sizes.
        foreach (var (label, bytes) in rows.Select(r => (r.SizeLabel, r.SizeBytes)).Distinct().OrderBy(t => t.SizeBytes))
        {
            double x = X(bytes);
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{T}\" x2=\"{F(x)}\" y2=\"{T + plotH}\" {gridAttr}/>");
            sb.Append($"<text x=\"{F(x)}\" y=\"{H - B + 18}\" {tickAttr} text-anchor=\"middle\">{label}</text>");
        }

        // Baseline.
        sb.Append($"<line x1=\"{L}\" y1=\"{T + plotH}\" x2=\"{W - R}\" y2=\"{T + plotH}\" {axisAttr}/>");

        // Series lines and hoverable markers.
        foreach (var s in series)
        {
            if (s.Points.Count == 0)
                continue;
            string dash = s.Dashed ? " stroke-dasharray=\"6 4\"" : "";
            string poly = string.Join(" ", s.Points.Select(p => $"{F(X(p.Item1))},{F(Y(p.Item2))}"));
            sb.Append($"<polyline points=\"{poly}\" {LineAttr(s.Slot)}{dash}/>");
            foreach (var (x, y) in s.Points)
            {
                string sizeLabel = rows.First(r => r.SizeBytes == (long)x).SizeLabel;
                sb.Append($"<circle cx=\"{F(X(x))}\" cy=\"{F(Y(y))}\" r=\"3.5\" {DotAttr(s.Slot)}>" +
                          $"<title>{Esc(s.Name)} at {sizeLabel}: {yFormat(y)}</title></circle>");
            }
        }

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

    // ---------- Standalone README assets ----------

    private const string TableStart = "<!-- BENCH:TABLE:START -->";
    private const string TableEnd = "<!-- BENCH:TABLE:END -->";

    /// <summary>
    /// Writes each chart as a standalone .svg per theme, plus the measurements table as Markdown,
    /// and splices that table into the README between marker comments — so the README can carry
    /// the full benchmark output on GitHub and one command refreshes it after every run.
    /// </summary>
    public static void WriteReadmeAssets(string resultsDir, string assetDir, string readmePath, TextWriter log)
    {
        // Checked before MergeWithPersisted, which would otherwise try to rewrite the results file
        // into a directory that does not exist — run from the wrong folder, that throws instead of
        // saying what is wrong.
        string resultsJson = Path.Combine(resultsDir, "transfer-results.json");
        if (!File.Exists(resultsJson))
        {
            log.WriteLine($"GraphReport: no persisted results at {resultsJson}; nothing to export.");
            log.WriteLine("  (run this from tests/QrShard.Benchmarks, after a benchmark session)");
            return;
        }

        var rows = MergeWithPersisted([], resultsJson);
        if (rows.Count == 0)
        {
            log.WriteLine("GraphReport: no persisted results found; nothing to export.");
            return;
        }

        Directory.CreateDirectory(assetDir);
        int written = 0;
        foreach (var chart in Charts(rows))
        {
            foreach (var palette in new[] { Palette.Light, Palette.Dark })
            {
                string body = ChartBody(chart.Series, rows, chart.YFormat, palette);
                if (body.Length == 0)
                    continue;
                File.WriteAllText(Path.Combine(assetDir, $"{chart.Slug}-{palette.Name}.svg"),
                    Standalone(chart, body, palette) + "\n");
                written++;
            }
        }

        string table = MarkdownTable(rows);
        File.WriteAllText(Path.Combine(assetDir, "measurements.md"), table);
        log.WriteLine();
        log.WriteLine($"// * README assets: {written} chart SVG(s) + measurements.md in {assetDir} *");
        SpliceReadme(readmePath, table, log);
    }

    /// <summary>Wraps a chart body in a self-contained SVG with its own title and legend baked in.</summary>
    private static string Standalone(Chart chart, string body, Palette p)
    {
        const int pad = 18;
        var legend = LegendRows(chart.Series);
        int top = 30 + legend.Count * 16;
        int h = top + ChartH;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {ChartW} {h}\" ")
          .Append($"width=\"{ChartW}\" height=\"{h}\" role=\"img\" aria-label=\"{Esc(chart.Title)}\">");
        sb.Append($"<rect width=\"{ChartW}\" height=\"{h}\" fill=\"{p.Surface}\"/>");
        sb.Append($"<text x=\"{pad}\" y=\"21\" font-family=\"{SvgFont}\" font-size=\"14.5\" ")
          .Append($"font-weight=\"600\" fill=\"{p.Ink}\">{Esc(chart.Title)}</text>");

        double y = 39;
        foreach (var row in legend)
        {
            double x = pad;
            foreach (var s in row)
            {
                string dash = s.Dashed ? " stroke-dasharray=\"5 3\"" : "";
                string color = p.Colors[s.Slot % p.Colors.Length];
                sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(y - 4)}\" x2=\"{F(x + 20)}\" y2=\"{F(y - 4)}\" ")
                  .Append($"stroke=\"{color}\" stroke-width=\"2.5\"{dash}/>");
                sb.Append($"<text x=\"{F(x + 26)}\" y=\"{F(y)}\" font-family=\"{SvgFont}\" font-size=\"11.5\" ")
                  .Append($"fill=\"{p.Ink2}\">{Esc(s.Name)}</text>");
                x += KeyWidth(s);
            }
            y += 16;
        }

        sb.Append($"<g transform=\"translate(0,{top})\">{body}</g></svg>");
        return sb.ToString();
    }

    /// <summary>Approximate advance width of one legend key, used to wrap the legend to the canvas.</summary>
    private static double KeyWidth(Series s) => 26 + s.Name.Length * 6.15 + 18;

    private static List<List<Series>> LegendRows(List<Series> series)
    {
        var rows = new List<List<Series>>();
        var current = new List<Series>();
        double x = 0, max = ChartW - 36;
        foreach (var s in series)
        {
            double w = KeyWidth(s);
            if (current.Count > 0 && x + w > max)
            {
                rows.Add(current);
                current = [];
                x = 0;
            }
            current.Add(s);
            x += w;
        }
        if (current.Count > 0)
            rows.Add(current);
        return rows;
    }

    private static string MarkdownTable(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| Size | Preset | Images | Encode | Decode | Codec MB/s | Est. manual (3 s/img) | Est. auto (0.5 s/img) |\n");
        sb.Append("|---|---|---:|---:|---:|---:|---:|---:|\n");
        foreach (var r in rows)
        {
            var (data, parity) = BenchPresets.EstimateImages(r.Preset, r.SizeBytes);
            string images = parity > 0 ? $"{data}+{parity}p" : data.ToString(CultureInfo.InvariantCulture);
            string mbps = r.Complete ? Compact(r.SizeBytes / 1048576.0 / r.CodecSec) : "—";
            sb.Append($"| {r.SizeLabel} | {r.Preset} | {images} | {Cell(r.EncodeSec)} | {Cell(r.DecodeSec)} ")
              .Append($"| {mbps} | {Cell(EstimatedTransferSec(r, 3.0))} | {Cell(EstimatedTransferSec(r, 0.5))} |\n");
        }
        return sb.ToString();

        static string Cell(double seconds) => double.IsNaN(seconds) ? "—" : FormatSeconds(seconds);
    }

    private static void SpliceReadme(string readmePath, string table, TextWriter log)
    {
        if (!File.Exists(readmePath))
        {
            log.WriteLine($"GraphReport: README not found at {readmePath}; table not spliced.");
            return;
        }
        string text = File.ReadAllText(readmePath);
        int start = text.IndexOf(TableStart, StringComparison.Ordinal);
        int end = text.IndexOf(TableEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            log.WriteLine($"GraphReport: {TableStart} / {TableEnd} markers not found; table not spliced.");
            return;
        }

        // Match the file's existing convention rather than forcing LF into a CRLF README —
        // core.autocrlf would hide that here, but not for a contributor without it.
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string body = nl == "\n" ? table : table.Replace("\n", nl);
        File.WriteAllText(readmePath, text[..(start + TableStart.Length)] + nl + body + text[end..]);
        log.WriteLine($"// * Spliced measurements table into {readmePath} *");
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string MachineSpecTable()
    {
        var sb = new StringBuilder();
        sb.Append("<table class=\"spec\"><tbody>");
        foreach (var (label, value) in MachineSpec.Collect())
            sb.Append($"<tr><th>{label}</th><td>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>");
        sb.Append("</tbody></table>");
        return sb.ToString();
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
          .spec { font-size: 12px; margin: 8px 0 4px; }
          .spec th { white-space: nowrap; width: 1%; vertical-align: top; text-align: left; }
          .spec td { text-align: left; }
        </style></head><body class="viz"><main>
        <h1>QrShard transfer benchmarks</h1>
        <p class="meta">Generated {{DateTime.Now:yyyy-MM-dd HH:mm}}</p>
        {{MachineSpecTable()}}
        """;
}
