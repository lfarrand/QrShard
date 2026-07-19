namespace QrShard;

/// <summary>Encodes a file into shard images.</summary>
internal interface IShardEncoder
{
    EncodeResult Encode(string filePath, string outDir, EncodeOptions options, Action<string>? log = null);
}

/// <summary>Decodes captured shard images back into the original file(s).</summary>
internal interface IShardDecoder
{
    List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log);

    DecodedShard DecodeImage(string path, DecodeScratch scratch);

    DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path);
}

/// <summary>Yields the frames of a recording (video file or animated image) in display order.</summary>
internal interface IFrameSource
{
    IEnumerable<Bitmap> Frames(string path, double fps);
}

/// <summary>Decodes shards from a recording (video file or animated image) of the slideshow.</summary>
internal interface IVideoDecoder
{
    List<RestoredFile> Decode(string path, string? outputPath, double extractFps, Action<string> log, out VideoDecodeStats stats);
}

/// <summary>Rectifies a camera photo of a camera-profile shard into an axis-aligned bitmap.</summary>
internal interface ICameraRectifier
{
    /// <summary>Rectified bitmap, or null when the image carries no detectable finder patterns.</summary>
    Bitmap? TryRectify(Bitmap photo);
}

/// <summary>Writes the sender-side slideshow page for video-mode transfers.</summary>
internal interface ISlideshowWriter
{
    string Write(string outDir, IReadOnlyList<string> imageFiles, int intervalMs);
}
