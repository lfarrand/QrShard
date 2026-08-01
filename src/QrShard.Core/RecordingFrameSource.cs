using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
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
    internal const long MaxAnimatedDecodedBytes = 256L * 1024 * 1024;
    internal const int MaxAnimatedFrames = 4096;
    private readonly int decodeMemoryBudgetMB;

    public RecordingFrameSource() : this(AppSettings.Current)
    {
    }

    public RecordingFrameSource(AppSettings settings) : this(settings.DecodeMemoryBudgetMB)
    {
    }

    internal RecordingFrameSource(int decodeMemoryBudgetMB)
    {
        this.decodeMemoryBudgetMB = decodeMemoryBudgetMB;
    }

    public IEnumerable<Bitmap> Frames(string path, double fps, CancellationToken cancellationToken = default) =>
        VideoDecoder.IsVideoFile(path)
            ? FfmpegFrames(path, fps, cancellationToken)
            : AnimatedImageFrames(path, cancellationToken);

    private IEnumerable<Bitmap> AnimatedImageFrames(string path, CancellationToken cancellationToken)
    {
        // Fifth site. Same hostile bytes as the folder path, and it had no filter at all.
        Image<Rgb24> image;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ImageSharp's animated-image API materializes every frame before frame 1 can be
            // yielded. Use one open handle for Identify + Load (no path replacement race), derive
            // the permitted Rgb24 frame count from the canvas, then ask for one extra frame. That
            // extra detects an over-limit animation while bounding peak pixel memory to the limit
            // plus one frame. ImageInfo.GetPixelMemorySize cannot be used here: for an indexed GIF
            // it reports 1 byte/pixel even though Load<Rgb24> necessarily retains 3.
            using var encoded = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = Image.Identify(new DecoderOptions { MaxFrames = 1, SkipMetadata = true }, encoded);
            long bytesPerFrame = checked((long)info.Width * info.Height * 3);
            ShardDecoder.ValidateImageDimensions(info.Width, info.Height, decodeMemoryBudgetMB);
            if (bytesPerFrame <= 0)
                throw new ShardDecodeException("Animated image declares invalid frame dimensions.");
            long decodedBudget = AnimatedDecodedBudget(decodeMemoryBudgetMB);
            // ImageSharp retains every requested frame and each yielded Bitmap needs one more
            // Rgb24 surface. Reserve one frame for that copy. Asking for one additional frame is
            // how an over-limit animation is detected without ever materializing beyond the cap.
            long permittedFrames = AllowedAnimatedFrames(info.Width, info.Height, decodeMemoryBudgetMB);
            if (permittedFrames < 1)
                throw new ShardDecodeException(
                    $"One animated-image frame needs about {bytesPerFrame / (1024 * 1024):N0} MiB of Rgb24 pixels; " +
                    $"the materialized frames plus one output frame exceed the {decodeMemoryBudgetMB:N0} MB " +
                    "DecodeMemoryBudgetMB safety limit. " +
                    "Use the file-referencing HTML slideshow or extract a bounded set of frames.");

            uint probeFrames = (uint)Math.Min(uint.MaxValue, permittedFrames + 1);
            encoded.Position = 0;
            image = Image.Load<Rgb24>(new DecoderOptions
            {
                MaxFrames = probeFrames,
                SkipMetadata = true,
            }, encoded);
            if (image.Frames.Count > permittedFrames)
            {
                image.Dispose();
                throw new ShardDecodeException(
                    $"Animated image exceeds the {decodedBudget / (1024 * 1024):N0} MiB decoded-memory or " +
                    $"{MaxAnimatedFrames:N0}-frame safety limit. " +
                    "Use the file-referencing HTML slideshow or extract a bounded set of frames.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException
                                       and not ShardDecodeException)
        {
            throw new ShardDecodeException($"'{Path.GetFileName(path)}' is not a readable image ({ex.Message}).");
        }
        using (image)
        for (int i = 0; i < image.Frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = image.Frames[i];
            var px = new Rgb24[frame.Width * frame.Height];
            frame.CopyPixelDataTo(px);
            yield return new Bitmap(px, frame.Width, frame.Height);
        }
    }

    private IEnumerable<Bitmap> FfmpegFrames(string path, double fps, CancellationToken cancellationToken) =>
        FfmpegPipe(["-i", path], fps, decodeMemoryBudgetMB: decodeMemoryBudgetMB,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Streams frames out of anything ffmpeg can open — a file, or a live capture device —
    /// as uncompressed BMP over a pipe (BMP because its header carries the exact file size,
    /// making stream framing trivial). Disposing the enumerator kills ffmpeg, which is how
    /// early stop avoids demuxing the rest (or, live, how the capture ends).
    /// </summary>
    internal static IEnumerable<Bitmap> FfmpegPipe(IReadOnlyList<string> inputArgs, double fps,
        string? extraFilter = null, int? decodeMemoryBudgetMB = null,
        CancellationToken cancellationToken = default)
    {
        string filter = $"fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                        + (extraFilter is null ? "" : "," + extraFilter);
        ProcessStartInfo psi = BuildFfmpegStartInfo(inputArgs, filter);

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

        // This method is an iterator, so process creation is deferred until the caller actually
        // enumerates and the child is immediately owned by ReadFrames' try/finally.
        foreach (Bitmap frame in ReadFrames(process,
                     decodeMemoryBudgetMB ?? AppSettings.Current.DecodeMemoryBudgetMB, cancellationToken))
            yield return frame;
    }

    internal static long AnimatedDecodedBudget(int budgetMB) =>
        Math.Min(MaxAnimatedDecodedBytes, checked(budgetMB * 1_000_000L));

    internal static long AllowedAnimatedFrames(int width, int height, int budgetMB)
    {
        ShardDecoder.ValidateImageDimensions(width, height, budgetMB);
        long bytesPerFrame = checked((long)width * height * 3);
        if (bytesPerFrame == 0)
            return 0;
        return Math.Max(0, Math.Min(MaxAnimatedFrames,
            AnimatedDecodedBudget(budgetMB) / bytesPerFrame - 1));
    }

    /// <summary>
    /// Builds ffmpeg's argv without ever concatenating user-controlled paths/device names into an
    /// argument string. A quote in a legal Unix filename used to split one -i value into injected
    /// ffmpeg options even though no shell was involved.
    /// </summary>
    internal static ProcessStartInfo BuildFfmpegStartInfo(IReadOnlyList<string> inputArgs, string filter)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error" })
            psi.ArgumentList.Add(argument);
        foreach (string argument in inputArgs)
            psi.ArgumentList.Add(argument);
        foreach (string argument in new[] { "-vf", filter, "-c:v", "bmp", "-f", "image2pipe", "-" })
            psi.ArgumentList.Add(argument);
        return psi;
    }

    private static IEnumerable<Bitmap> ReadFrames(Process process, int decodeMemoryBudgetMB,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TryStopProcess((Process)state!), process);
        try
        {
            // Both streams are redirected. If stderr is not drained while stdout is read, ffmpeg
            // eventually fills its stderr pipe and blocks forever waiting for space. Keep only a
            // bounded tail for diagnostics; the asynchronous handler continuously drains the rest.
            const int MaxErrorTailChars = 4096;
            var errorTail = new StringBuilder();
            object errorLock = new();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (errorLock)
                {
                    errorTail.AppendLine(e.Data);
                    if (errorTail.Length > MaxErrorTailChars)
                        errorTail.Remove(0, errorTail.Length - MaxErrorTailChars);
                }
            };
            process.BeginErrorReadLine();

            var stdout = process.StandardOutput.BaseStream;
            const int BmpHeaderBytes = 54; // ffmpeg's bmp encoder emits BITMAPINFOHEADER
            var header = new byte[BmpHeaderBytes];
            bool producedFrame = false;
            while (true)
            {
                if (!ReadExactly(stdout, header, BmpHeaderBytes, cancellationToken))
                    break;
                if (header[0] != (byte)'B' || header[1] != (byte)'M')
                    throw new ShardDecodeException("Unexpected data in the ffmpeg frame stream.");
                int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
                if (size < BmpHeaderBytes || size > 512_000_000)
                    throw new ShardDecodeException("Implausible frame size in the ffmpeg stream.");

                int dibSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14));
                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18));
                int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22));
                if (dibSize < 40 || width < 1 || signedHeight is 0 or int.MinValue)
                    throw new ShardDecodeException("Implausible BMP geometry in the ffmpeg frame stream.");
                int height = Math.Abs(signedHeight);
                ShardDecoder.ValidateImageDimensions(width, height, decodeMemoryBudgetMB);
                long planned = checked((long)size + (long)width * height * 6);
                long budget = checked(decodeMemoryBudgetMB * 1_000_000L);
                if (planned > budget)
                    throw new ShardDecodeException(
                        $"ffmpeg frame is {width:N0}x{height:N0} (~{planned / 1_000_000:N0} MB including the BMP and " +
                        $"two Rgb24 surfaces), above the {decodeMemoryBudgetMB:N0} MB DecodeMemoryBudgetMB.");

                var bmp = new byte[size];
                header.CopyTo(bmp, 0);
                if (!ReadExactly(stdout, bmp.AsSpan(BmpHeaderBytes, size - BmpHeaderBytes), cancellationToken))
                    break;

                // Sixth site. A torn or truncated frame is a NORMAL event in a screen recording,
                // so this one skips rather than throws: ending the enumeration here would discard
                // every shard already collected from the recording.
                Bitmap decoded;
                try
                {
                    using var image = Image.Load<Rgb24>(bmp);
                    if (image.Width != width || image.Height != height)
                        throw new ShardDecodeException("BMP dimensions changed while decoding the ffmpeg frame.");
                    var px = new Rgb24[image.Width * image.Height];
                    image.CopyPixelDataTo(px);
                    decoded = new Bitmap(px, image.Width, image.Height);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    continue;
                }
                producedFrame = true;
                yield return decoded;
            }

            process.WaitForExit(); // also waits for the asynchronous stderr drain to finish
            if (!cancellationToken.IsCancellationRequested && !producedFrame && process.ExitCode != 0)
            {
                string detail;
                lock (errorLock)
                    detail = errorTail.ToString().Trim();
                throw new ShardDecodeException("ffmpeg could not decode the recording" +
                    (detail.Length == 0 ? "." : $": {ShardHeader.Display(detail)}"));
            }
        }
        finally
        {
            TryStopProcess(process); // early stop: no need to demux the rest of the recording
            process.Dispose();
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                         NotSupportedException or ObjectDisposedException)
        {
            // Best effort: cancellation may race natural process exit or final disposal.
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count,
        CancellationToken cancellationToken) =>
        ReadExactly(stream, buffer.AsSpan(0, count), cancellationToken);

    private static bool ReadExactly(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            int n;
            try
            {
                n = stream.Read(buffer[offset..]);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
                                      ex is IOException or ObjectDisposedException)
            {
                return false; // cancellation killed ffmpeg and closed/broke its stdout pipe
            }
            if (n == 0)
                return false;
            offset += n;
        }
        return true;
    }
}
