using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QrShard.Recorder;

/// <summary>Captures every DXGI present on one monitor to lossless image files.</summary>
internal static class Program
{
    private const int AcquireTimeoutMs = 500;
    private const int SaveQueueCapacitySdr = 64;
    private const int SaveQueueCapacityHdr = 16;
    private const nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static long s_saved;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: QrShard.Recorder <output-folder>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Captures every frame the monitor displays (DXGI desktop duplication),");
            Console.Error.WriteLine("saving lossless images named yyyyMMdd-N into <output-folder> in the");
            Console.Error.WriteLine("format configured in App.config (png, tiff, or bmp). Recording stops");
            Console.Error.WriteLine("when the whole screen shows the termination colour configured in");
            Console.Error.WriteLine("App.config (default #000000), or on Ctrl+C.");
            return 1;
        }

        string outputFolder = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputFolder);

        Color termination = LoadTerminationColor();
        var terminationRgb = new Rgb(termination.R, termination.G, termination.B);
        int terminationTolerance = SettingParsers.ParseTolerance(
            ReadSetting("TerminationTolerance"));
        bool hdrCapture = SettingParsers.ParseBool(ReadSetting("HdrCapture"));

        string? format = OutputFormatParser.TryNormalize(ReadSetting("OutputFormat"));
        if (format is null)
        {
            Console.Error.WriteLine($"OutputFormat '{ConfigurationManager.AppSettings["OutputFormat"]}' " +
                                    "in App.config is not supported. Lossless formats: png, tiff, bmp.");
            return 1;
        }
        if (hdrCapture && format != "tiff")
        {
            Console.WriteLine($"HdrCapture stores 32-bit float scRGB, which only TIFF holds - " +
                              $"OutputFormat '{format}' is ignored.");
            format = "tiff";
        }
        string extension = format == "tiff" ? "tif" : format;

        if (!SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
        {
            SetProcessDPIAware();
        }

        var screens = System.Windows.Forms.Screen.AllScreens;
        Console.WriteLine("Detected screens:");
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            Console.WriteLine($"  {i + 1}: {screens[i].DeviceName} {b.Width}x{b.Height} at ({b.X},{b.Y})" +
                              (screens[i].Primary ? " [primary]" : ""));
        }

        int screenIndex = SettingParsers.ParseScreenIndex(ReadSetting("CaptureScreen"));
        System.Windows.Forms.Screen? captureScreen = screenIndex switch
        {
            0 => System.Windows.Forms.Screen.PrimaryScreen ?? screens[0],
            _ when screenIndex <= screens.Length => screens[screenIndex - 1],
            _ => null,
        };
        if (captureScreen is null)
        {
            Console.Error.WriteLine($"CaptureScreen {screenIndex} in App.config is out of range - " +
                                    $"only {screens.Length} screen(s) detected.");
            return 1;
        }

        DesktopDuplicator duplicator;
        try
        {
            duplicator = DesktopDuplicator.Create(captureScreen.DeviceName, hdrCapture);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DXGI desktop duplication unavailable for {captureScreen.DeviceName}: {ex.Message}" +
                                    (ex.InnerException is { } inner ? $" [{inner.Message}]" : ""));
            return 1;
        }

        using var _ = duplicator;
        int width = duplicator.Width;
        int height = duplicator.Height;
        int frameBytes = width * height * duplicator.BytesPerPixel;
        CapturePixelFormat pixelKind = duplicator.PixelFormat;

        Console.WriteLine($"Recording {captureScreen.DeviceName} ({width}x{height}) to {outputFolder} as .{extension}" +
                          (hdrCapture ? $" ({pixelKind} -> 32-bit float scRGB TIFF)" : ""));
        if (duplicator.ColorSpaceNote is { } colorSpaceNote)
        {
            Console.WriteLine(colorSpaceNote);
        }
        Console.WriteLine($"Stops when the screen is uniformly {ColorTranslator.ToHtml(termination)} " +
                          $"(+/-{terminationTolerance} per channel), or on Ctrl+C.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var bufferPool = new ConcurrentBag<byte[]>();
        for (int i = 0; i < (hdrCapture ? 4 : 8); i++)
        {
            bufferPool.Add(new byte[frameBytes]);
        }
        byte[] RentBuffer() => bufferPool.TryTake(out var b) ? b : new byte[frameBytes];

        using var saveQueue = new BlockingCollection<(byte[] Buffer, string Path)>(
            hdrCapture ? SaveQueueCapacityHdr : SaveQueueCapacitySdr);
        int encoderCount = Math.Max(2, Environment.ProcessorCount - 2);
        var encoders = new Thread[encoderCount];
        for (int i = 0; i < encoderCount; i++)
        {
            encoders[i] = new Thread(() => EncodeWorker(saveQueue, bufferPool, width, height, format, pixelKind))
            {
                IsBackground = true,
                Name = $"encoder-{i}",
            };
            encoders[i].Start();
        }

        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        int pixelCount = width * height;
        bool IsTermination(byte[] frame) => pixelKind switch
        {
            CapturePixelFormat.Fp16 => TerminationMatch.IsUniformFp16(frame, pixelCount, terminationRgb, terminationTolerance),
            CapturePixelFormat.Pq10 => TerminationMatch.IsUniformPq10(frame, pixelCount, terminationRgb, terminationTolerance),
            _ => TerminationMatch.IsUniformBgra8(frame, pixelCount, terminationRgb, terminationTolerance),
        };

        string date = DateTime.Now.ToString("yyyyMMdd");
        int sequence = CaptureSequence.Next(outputFolder, date, extension);
        long captured = 0;
        bool queueFullWarned = false;
        var statusWatch = Stopwatch.StartNew();
        (long, long, long) lastStatus = (-1, -1, -1);

        while (!cts.IsCancellationRequested)
        {
            byte[] buffer = RentBuffer();

            if (!duplicator.TryAcquireFrame(buffer, AcquireTimeoutMs))
            {
                bufferPool.Add(buffer);
            }
            else if (IsTermination(buffer))
            {
                bufferPool.Add(buffer);
                Console.WriteLine("Termination colour detected - stopping.");
                break;
            }
            else
            {
                string today = DateTime.Now.ToString("yyyyMMdd");
                if (today != date)
                {
                    date = today;
                    sequence = CaptureSequence.Next(outputFolder, date, extension);
                }

                string path = Path.Combine(outputFolder, $"{date}-{sequence}.{extension}");
                sequence++;
                captured++;

                if (!saveQueue.TryAdd((buffer, path)))
                {
                    if (!queueFullWarned)
                    {
                        queueFullWarned = true;
                        Console.WriteLine("Warning: encoders can't keep up - capture will stall " +
                                          "until the queue drains, and frames may be missed.");
                    }
                    saveQueue.Add((buffer, path));
                }
            }

            if (statusWatch.ElapsedMilliseconds >= 1000)
            {
                statusWatch.Restart();
                var status = (captured, Volatile.Read(ref s_saved), duplicator.MissedFrames);
                if (status != lastStatus)
                {
                    lastStatus = status;
                    Console.WriteLine($"captured {status.Item1}, saved {status.Item2}, " +
                                      $"queued {status.Item1 - status.Item2}, missed {status.Item3}");
                }
            }
        }

        saveQueue.CompleteAdding();
        foreach (Thread encoder in encoders)
        {
            encoder.Join();
        }

        Console.WriteLine($"Recorder finished: {Volatile.Read(ref s_saved)} frame(s) saved" +
                          (duplicator.MissedFrames > 0
                              ? $", {duplicator.MissedFrames} frame(s) missed (see AccumulatedFrames)."
                              : ", no frames missed."));
        return 0;
    }

    private static void EncodeWorker(BlockingCollection<(byte[] Buffer, string Path)> queue,
        ConcurrentBag<byte[]> bufferPool, int width, int height, string format, CapturePixelFormat kind)
    {
        bool hdr = kind != CapturePixelFormat.Bgra8;
        WriteableBitmap? bitmap = hdr
            ? null
            : new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var fullFrame = new Int32Rect(0, 0, width, height);
        float[]? floats = hdr ? new float[width * height * 4] : null;

        foreach (var (buffer, path) in queue.GetConsumingEnumerable())
        {
            try
            {
                if (hdr)
                {
                    if (kind == CapturePixelFormat.Fp16)
                    {
                        var halfs = MemoryMarshal.Cast<byte, Half>(buffer.AsSpan(0, width * height * 8));
                        for (int i = 0; i < floats!.Length; i++)
                        {
                            floats[i] = (float)halfs[i];
                        }
                    }
                    else
                    {
                        ScRgb.ConvertPq10ToScRgb(buffer, floats!, width * height);
                    }
                    bufferPool.Add(buffer);

                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                        FileShare.None, 1 << 20);
                    FloatTiffWriter.Write(stream, floats!, width, height);
                }
                else
                {
                    bitmap!.WritePixels(fullFrame, buffer, width * 4, 0);
                    bufferPool.Add(buffer);

                    BitmapEncoder encoder = CreateEncoder(format);
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                        FileShare.None, 1 << 20);
                    encoder.Save(stream);
                }
                Interlocked.Increment(ref s_saved);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to save {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private static BitmapEncoder CreateEncoder(string format) => format switch
    {
        "png" => new PngBitmapEncoder(),
        "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
        "bmp" => new BmpBitmapEncoder(),
        _ => throw new InvalidOperationException($"unknown format {format}"),
    };

    private static string? ReadSetting(string key)
    {
        try
        {
            return ConfigurationManager.AppSettings[key];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Color LoadTerminationColor()
    {
        try
        {
            string? text = ReadSetting("TerminationColor");
            if (!string.IsNullOrWhiteSpace(text))
                return ColorTranslator.FromHtml(text);
        }
        catch (Exception)
        {
            // Missing or malformed config - fall back to the default below.
        }
        return Color.Black;
    }
}
