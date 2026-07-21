using System.Text;
using System.Text.Json;

namespace QrShard;

/// <summary>
/// Machine-readable output for the verify and info commands, written with Utf8JsonWriter
/// directly (no reflection — trims and AOT-compiles cleanly).
/// </summary>
internal sealed class JsonReports
{
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

    public string VerifyReport(List<DecodedShard> shards, IParityReassembler parity)
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
