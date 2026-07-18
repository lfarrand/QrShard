using System.Buffers.Binary;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

internal sealed record VideoDecodeStats(int FramesExamined, int FramesDecoded, int ShardsCollected, bool StoppedEarly);

/// <summary>
/// Decodes shards from a recording of the slideshow — the receiver-side half of video mode.
///
/// Frame sources: animated images (APNG/GIF/WebP) are read natively via ImageSharp; real video
/// containers (mp4/webm/mkv/mov/avi) are demuxed by ffmpeg streaming uncompressed BMP frames
/// over a pipe (nothing is written to disk, and killing the process implements early stop).
///
/// Two optimizations make hour-long recordings cheap:
///  - a tiny downsampled-luminance pre-filter skips frames nearly identical to the previous
///    one (a 30 fps recording of a 2 img/s slideshow is ~94% duplicates);
///  - decoding stops the moment the collected shard set is complete or recoverable via parity.
/// Torn mid-transition frames simply fail CRC/ECC and are skipped; the loop guarantees the
/// same shard comes around again.
/// </summary>
internal static class VideoDecoder
{
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v"];

    /// <summary>Mean-abs-luminance difference (0-255) below which a frame is treated as a duplicate.</summary>
    private const double DuplicateThreshold = 3.0;

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>An image file whose container holds more than one frame (APNG/GIF/WebP animation).</summary>
    public static bool IsAnimatedImage(string path)
    {
        try
        {
            var info = Image.Identify(path);
            return info.FrameMetadataCollection.Count > 1;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }

    public static List<RestoredFile> Decode(string path, string? outputPath, double extractFps, Action<string> log,
        out VideoDecodeStats stats)
    {
        var frames = IsVideoFile(path) ? FfmpegFrames(path, extractFps) : AnimatedImageFrames(path);
        var shards = CollectShards(frames, log, out stats);
        log($"  video: examined {stats.FramesExamined} frame(s), fully decoded {stats.FramesDecoded}, " +
            $"collected {stats.ShardsCollected} shard(s){(stats.StoppedEarly ? ", stopped early — set complete" : "")}");
        if (shards.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found in the video.");
        return Decoder.Assemble(shards, outputPath, log);
    }

    // ---------- Shard collection with dedupe + early stop ----------

    private static List<DecodedShard> CollectShards(IEnumerable<Bitmap> frames, Action<string> log, out VideoDecodeStats stats)
    {
        var scratch = new Decoder.DecodeScratch();
        var shards = new List<DecodedShard>();
        var seen = new HashSet<(ulong FileId, int Index, bool Parity)>();
        byte[]? previousSignature = null;
        int examined = 0, decoded = 0;
        bool stoppedEarly = false;

        foreach (var frame in frames)
        {
            examined++;
            byte[] signature = FrameSignature(frame);
            bool duplicate = previousSignature is not null && MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
            previousSignature = signature;
            if (duplicate)
                continue;

            decoded++;
            try
            {
                var shard = Decoder.DecodeBitmap(frame, scratch, $"frame {examined}");
                if (seen.Add((shard.Header.FileId, shard.Header.Index, shard.Header.IsParity)))
                {
                    shards.Add(shard);
                    string which = shard.Header.IsParity
                        ? $"parity #{shard.Header.Index + 1}"
                        : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
                    log($"  ok      frame {examined}  ({which}, {shard.Payload.Length:N0} bytes)");

                    if (Decoder.IsSetComplete(shards))
                    {
                        stoppedEarly = true;
                        break;
                    }
                }
            }
            catch (ShardDecodeException)
            {
                // Torn/blended transition frame, or junk between shards — the loop will bring
                // the shard around again, so silently skip.
            }
        }

        stats = new VideoDecodeStats(examined, decoded, shards.Count, stoppedEarly);
        return shards;
    }

    /// <summary>
    /// Sparse point-sample signature for cheap near-duplicate rejection: the luminance of 1024
    /// exact pixels on a fixed grid. Point samples, deliberately not averages — shard content
    /// is noise-like, so any downsampled average converges to the same mean for every shard,
    /// while exact pixels differ almost everywhere between different shards.
    /// </summary>
    private static byte[] FrameSignature(Bitmap frame)
    {
        const int grid = 32;
        var signature = new byte[grid * grid];
        for (int gy = 0; gy < grid; gy++)
        {
            int y = (2 * gy + 1) * frame.Height / (2 * grid);
            for (int gx = 0; gx < grid; gx++)
            {
                int x = (2 * gx + 1) * frame.Width / (2 * grid);
                var p = frame.At(x, y);
                signature[gy * grid + gx] = (byte)((p.R + p.G + p.B) / 3);
            }
        }
        return signature;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    // ---------- Frame sources ----------

    private static IEnumerable<Bitmap> AnimatedImageFrames(string path)
    {
        using var image = Image.Load<Rgb24>(path);
        for (int i = 0; i < image.Frames.Count; i++)
        {
            var frame = image.Frames[i];
            var px = new Rgb24[frame.Width * frame.Height];
            frame.CopyPixelDataTo(px);
            yield return new Bitmap(px, frame.Width, frame.Height);
        }
    }

    /// <summary>
    /// Streams frames out of any container ffmpeg understands, as uncompressed BMP over a pipe
    /// (BMP because its header carries the exact file size, making stream framing trivial).
    /// Disposing the enumerator kills ffmpeg, which is how early stop avoids demuxing the rest.
    /// </summary>
    private static IEnumerable<Bitmap> FfmpegFrames(string path, double fps)
    {
        ProcessStartInfo psi = new("ffmpeg",
            $"-hide_banner -loglevel error -i \"{path}\" -vf fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -c:v bmp -f image2pipe -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ShardDecodeException("Failed to start ffmpeg.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ShardDecodeException(
                "Decoding video files requires ffmpeg on PATH (https://ffmpeg.org). " +
                "Alternatively, extract frames yourself and decode the folder.");
        }

        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var header = new byte[6];
            while (true)
            {
                if (!ReadExactly(stdout, header, 6))
                    break;
                if (header[0] != (byte)'B' || header[1] != (byte)'M')
                    throw new ShardDecodeException("Unexpected data in the ffmpeg frame stream.");
                int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
                if (size < 30 || size > 512_000_000)
                    throw new ShardDecodeException("Implausible frame size in the ffmpeg stream.");

                var bmp = new byte[size];
                header.CopyTo(bmp, 0);
                if (!ReadExactly(stdout, bmp.AsSpan(6, size - 6)))
                    break;

                using var image = Image.Load<Rgb24>(bmp);
                var px = new Rgb24[image.Width * image.Height];
                image.CopyPixelDataTo(px);
                yield return new Bitmap(px, image.Width, image.Height);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(); // early stop: no need to demux the rest of the recording
                process.Dispose();
            }
            catch
            {
                // best effort
            }
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count) =>
        ReadExactly(stream, buffer.AsSpan(0, count));

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = stream.Read(buffer[offset..]);
            if (n == 0)
                return false;
            offset += n;
        }
        return true;
    }
}
