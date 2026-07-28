using System.Text;
using System.Text.Json;

namespace QrShard;

/// <summary>
/// Machine-readable output for the encode, decode, verify and info commands, written with
/// Utf8JsonWriter directly (no reflection — trims and AOT-compiles cleanly).
/// </summary>
internal sealed class JsonReports
{
    public string DryRunReport(EncodePlan plan, string outputDir)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("dryRun", true);
            w.WriteString("outputDir", outputDir);
            w.WriteNumber("imageCount", plan.ImageCount);
            w.WriteNumber("dataImages", plan.DataImages);
            w.WriteNumber("recoveryImages", plan.ParityImages);
            w.WriteNumber("bytesPerImage", plan.BytesPerImage);
            w.WriteNumber("width", plan.Width);
            w.WriteNumber("height", plan.Height);
            w.WriteNumber("stripeData", plan.StripeData);
            w.WriteNumber("stripeParity", plan.StripeParity);
            w.WriteString("format", plan.Format);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string EncodeReport(EncodeResult result, string outputDir, string? slideshowPath)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("outputDir", outputDir);
            w.WriteNumber("imageCount", result.ImageCount);
            w.WriteNumber("dataImages", result.DataImages);
            w.WriteNumber("parityImages", result.ParityImages);
            w.WriteNumber("bytesPerImage", result.BytesPerImage);
            w.WriteNumber("width", result.Width);
            w.WriteNumber("height", result.Height);
            w.WriteNumber("stripeData", result.StripeData);
            w.WriteNumber("stripeParity", result.StripeParity);
            if (slideshowPath is not null)
                w.WriteString("slideshow", slideshowPath);
            w.WriteStartArray("files");
            foreach (string f in result.Files)
                w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// A completed decode: what was written and where. <c>outputPath</c> is the whole point —
    /// without an explicit -o the destination is resolved from the shard header and may fall
    /// back to "&lt;name&gt;.restored&lt;ext&gt;", so it is not something a script can predict.
    /// </summary>
    public string DecodeReport(List<RestoredFile> restored)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("complete", true);
            w.WriteStartArray("restored");
            foreach (var f in restored)
            {
                w.WriteStartObject();
                w.WriteString("fileName", f.FileName);
                w.WriteString("outputPath", f.OutputPath);
                w.WriteNumber("length", f.Length);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// A decode that stopped short (exit 3): the same per-file status verify reports, so a script
    /// reads one shape either way. No "restored" key — Assemble writes any complete file it meets
    /// before throwing on an incomplete sibling, and that partial list does not survive the throw,
    /// so claiming a restored set here would be a guess.
    /// </summary>
    public string DecodeIncompleteReport(List<DecodedShard> shards, IParityReassembler parity) =>
        SetStatusReport(shards, parity);

    public string VerifyReport(List<DecodedShard> shards, IParityReassembler parity) =>
        SetStatusReport(shards, parity);

    private static string SetStatusReport(List<DecodedShard> shards, IParityReassembler parity)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("complete", shards.Count > 0 && parity.IsSetComplete(shards));
            w.WriteStartArray("files");
            foreach (var group in shards.GroupBy(s => s.Header.FileId))
            {
                var first = group.First().Header;
                var have = group.Where(s => !s.Header.IsParity).Select(s => s.Header.Index).ToHashSet();
                w.WriteStartObject();
                w.WriteString("fileName", first.FileName);
                w.WriteString("fileId", first.FileId.ToString("x16"));
                w.WriteNumber("dataPresent", have.Count);
                w.WriteNumber("dataTotal", first.Count);
                w.WriteNumber("parityPresent", group.Count(s => s.Header.IsParity));
                w.WriteBoolean("recoverable", parity.IsSetComplete([.. group]));
                w.WriteStartArray("missing");
                foreach (int i in Enumerable.Range(0, first.Count).Where(i => !have.Contains(i)))
                    w.WriteNumberValue(i + 1);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string InfoReport(DecodedShard shard, string? heatmapPath, int correctedCodewords, int failedCodewords)
    {
        var h = shard.Header;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("fileName", h.FileName);
            w.WriteString("fileId", h.FileId.ToString("x16"));
            w.WriteBoolean("isParity", h.IsParity);
            w.WriteNumber("index", h.Index);
            w.WriteNumber("count", h.Count);
            w.WriteNumber("payloadLength", h.PayloadLength);
            w.WriteNumber("totalLength", h.TotalLength);
            w.WriteNumber("originalLength", h.OriginalLength);
            w.WriteNumber("stripeData", h.StripeData);
            w.WriteNumber("stripeParity", h.StripeParity);
            w.WriteBoolean("compressed", (h.Flags & ShardHeader.FlagCompressed) != 0);
            w.WriteBoolean("encrypted", (h.Flags & ShardHeader.FlagEncrypted) != 0);
            w.WriteBoolean("archive", (h.Flags & ShardHeader.FlagArchive) != 0);
            w.WriteBoolean("fountain", (h.Flags & ShardHeader.FlagFountain) != 0);
            w.WriteNumber("eccParity", shard.EccParity);
            w.WriteNumber("eccCorrectedBytes", shard.CorrectedBytes);
            w.WriteString("sha256", Convert.ToHexStringLower(h.Sha256));
            if (heatmapPath is not null)
            {
                w.WriteString("heatmap", heatmapPath);
                w.WriteNumber("correctedCodewords", correctedCodewords);
                w.WriteNumber("failedCodewords", failedCodewords);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
