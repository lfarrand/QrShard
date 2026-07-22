using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Encodes a file into shard images.</summary>
internal interface IShardEncoder
{
    EncodeResult Encode(string filePath, string outDir, EncodeOptions options, Action<string>? log = null);

    EncodePlan Plan(string filePath, EncodeOptions options);
}

/// <summary>Decodes captured shard images back into the original file(s).</summary>
internal interface IShardDecoder
{
    List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log,
        string? password = null);

    List<DecodedShard> CollectShards(IEnumerable<string> imagePaths, Action<string> log);

    DecodedShard DecodeImage(string path, DecodeScratch scratch);

    DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path);

    DecodeDiagnostics Diagnose(string path);
}

/// <summary>Locates the shard's black locator frame and validates it against the metadata strip.</summary>
internal interface IFrameLocator
{
    (Layout Layout, InnerRect Inner) Locate(Bitmap bmp, DecodeScratch scratch);
}

/// <summary>Measures the frame's inner (white) edge with subpixel precision.</summary>
internal interface IInnerRectScanner
{
    InnerRect FindInnerRect(Bitmap bmp, PixelRect frame);
}

/// <summary>Reads the self-describing metadata strip and the palette calibration strips.</summary>
internal interface IStripReader
{
    Layout? ReadMetadata(Bitmap bmp, InnerRect inner);

    PaletteSet ReadPalette(Bitmap bmp, InnerRect inner, Layout layout);
}

/// <summary>Samples every data cell and classifies it against the measured palette.</summary>
internal interface IGridSampler
{
    byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes, DecodeScratch scratch,
        out bool[]? suspectBytes, out byte[]? secondChoiceBytes);
}

/// <summary>Reassembles decoded shards into output files.</summary>
internal interface IShardAssembler
{
    List<RestoredFile> Assemble(List<DecodedShard> shards, string? outputPath, Action<string> log, string? password = null);
}

/// <summary>Cross-shard-parity recovery and the stripe-aware completeness check.</summary>
internal interface IParityReassembler
{
    bool IsSetComplete(IReadOnlyCollection<DecodedShard> shards);

    byte[][] ReassembleWithParity(List<DecodedShard> shards, ShardHeader first, Action<string> log, out int chunkCapacity);
}

/// <summary>Decodes shards from a recording (video file or animated image) of the slideshow.</summary>
internal interface IVideoDecoder
{
    List<RestoredFile> Decode(string path, string? outputPath, double extractFps, Action<string> log,
        out VideoDecodeStats stats, string? password = null, int decodeWorkers = 1, bool escalateFps = false);
}

/// <summary>Yields the frames of a recording (video file or animated image) in display order.</summary>
internal interface IFrameSource
{
    IEnumerable<Bitmap> Frames(string path, double fps);
}

/// <summary>Rectifies a camera photo of a camera-profile shard into an axis-aligned bitmap.</summary>
internal interface ICameraRectifier
{
    /// <summary>Rectified bitmap, or null when the image carries no detectable finder patterns.</summary>
    Bitmap? TryRectify(Bitmap photo);

    /// <summary>Finder detection only — cacheable across video frames.</summary>
    CameraPose? DetectPose(Bitmap photo);

    /// <summary>Warp + refinement under a known (possibly cached) pose.</summary>
    Bitmap RectifyWithPose(Bitmap photo, CameraPose pose);
}

/// <summary>Guides encode-setting selection: emits density probes, analyzes their captures.</summary>
internal interface ICalibration
{
    int Generate(string outDir, int width, int height, bool camera, TextWriter output);

    int Analyze(string capturedFolder, TextWriter output);
}

/// <summary>Illumination-adaptive dark/light binarization of a photo.</summary>
internal interface IAdaptiveBinarizer
{
    bool[] Threshold(Bitmap photo);
}

/// <summary>Finds QR-style finder-pattern candidates in a binarized photo.</summary>
internal interface IFinderDetector
{
    List<FinderCluster> FindCandidates(Bitmap photo, bool[] dark);
}

/// <summary>Selects the finder rectangle from candidate clusters and resolves its orientation.</summary>
internal interface IQuadSelector
{
    FinderQuad? ChooseQuad(List<FinderCluster> clusters);

    OrientedQuad? ResolveOrientation(Bitmap photo, bool[] dark, FinderQuad quad);
}

/// <summary>Finds the approximate frame outer box in the coarse rectified canvas.</summary>
internal interface ICoarseFrameScanner
{
    (double X0, double Y0, double X1, double Y1)? FindFrameBox(Bitmap coarse, double module);
}

/// <summary>Traces one frame side in the photo, capturing residuals and reference colors.</summary>
internal interface IFrameEdgeTracer
{
    SideTrace? TraceSide(Bitmap photo, CanvasGeometry geometry,
        Func<int, (double X, double Y)> canvasPoint, (double X, double Y) outwardNormal);
}

/// <summary>Writes the sender-side slideshow page for video-mode transfers.</summary>
internal interface ISlideshowWriter
{
    string Write(string outDir, IReadOnlyList<string> imageFiles, int intervalMs);

    string WriteApng(string outDir, IReadOnlyList<string> imageFiles, int intervalMs);
}

/// <summary>End-to-end round-trip self-test, run via `qrshard test`.</summary>
internal interface ISelfTest
{
    bool Run();

    /// <summary>Round-trips the user's own file at their settings through simulated screenshots;
    /// returns a process exit code (0 = survived).</summary>
    int RunFile(string filePath, EncodeOptions options, TextWriter output);
}
