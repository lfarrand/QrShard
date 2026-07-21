using System.Buffers.Binary;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Default frame source for recordings: animated images (APNG/GIF/WebP) are read natively via
/// ImageSharp; real video containers (mp4/webm/mkv/mov/avi) are demuxed by ffmpeg streaming
/// uncompressed BMP frames over a pipe (nothing is written to disk, and killing the process
/// implements early stop).
/// </summary>
internal sealed class RecordingFrameSource : IFrameSource
{
    public IEnumerable<Bitmap> Frames(string path, double fps) =>
        VideoDecoder.IsVideoFile(path) ? FfmpegFrames(path, fps) : AnimatedImageFrames(path);

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

    private static IEnumerable<Bitmap> FfmpegFrames(string path, double fps) =>
        FfmpegPipe($"-i \"{path}\"", fps);

    /// <summary>
    /// Streams frames out of anything ffmpeg can open — a file, or a live capture device —
    /// as uncompressed BMP over a pipe (BMP because its header carries the exact file size,
    /// making stream framing trivial). Disposing the enumerator kills ffmpeg, which is how
    /// early stop avoids demuxing the rest (or, live, how the capture ends).
    /// </summary>
    internal static IEnumerable<Bitmap> FfmpegPipe(string inputArgs, double fps, string? extraFilter = null)
    {
        string filter = $"fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                        + (extraFilter is null ? "" : "," + extraFilter);
        ProcessStartInfo psi = new("ffmpeg",
            $"-hide_banner -loglevel error {inputArgs} -vf {filter} -c:v bmp -f image2pipe -")
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
