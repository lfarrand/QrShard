using QrShard;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// Regression tests for the adversarial-audit fixes: unbounded/overflowing derived sizes, the
/// unclamped border read, AggregateException opacity, slideshow format handling, and the
/// public-API exception contract.
/// </summary>
public class AuditFixTests
{
    // ---- Finding: crafted metadata-strip geometry overflows the grid buffer sizing ----

    private static bool[] ToModules(byte[] packed)
    {
        var m = new bool[Layout.MetaModuleCount];
        for (int i = 0; i < m.Length; i++)
            m[i] = (packed[i >> 3] & (0x80 >> (i & 7))) != 0;
        return m;
    }

    private static bool[] CraftStrip(int bits, int gridW, int gridH, int cellPx, int metaH, int innerW, int innerH, int ecc)
    {
        var w = new BitWriter();
        w.Write(Layout.MetaMagic, 8);
        w.Write((uint)Layout.MetaVersion, 4);
        w.Write((uint)bits, 4);
        w.Write((uint)gridW, 16);
        w.Write((uint)gridH, 16);
        w.Write((uint)cellPx, 8);
        w.Write((uint)metaH, 16);
        w.Write((uint)innerW, 16);
        w.Write((uint)innerH, 16);
        w.Write((uint)ecc, 8);
        byte[] payload = w.ToArray(); // 14 bytes
        w.Write(new Crc().Crc16Ccitt(payload), 16);
        return ToModules(w.ToArray());
    }

    [Fact]
    public void UnpackMetadata_ConsistentGeometry_IsAccepted()
    {
        // Control: a strip whose inner rect matches 2*metaH+gridW*cellPx / 6*metaH+gridH*cellPx.
        var strip = CraftStrip(bits: 4, gridW: 100, gridH: 100, cellPx: 3, metaH: 10,
            innerW: 2 * 10 + 100 * 3, innerH: 6 * 10 + 100 * 3, ecc: 16);
        Assert.NotNull(Layout.UnpackMetadata(strip));
    }

    [Fact]
    public void UnpackMetadata_OversizeGrid_IsRejected()
    {
        // GridW=GridH=65535 (the 16-bit max) would make GridSampler size
        // streamLength = (int)(65535*65535*8/8) which overflows int negative — must be rejected.
        var strip = CraftStrip(bits: 8, gridW: 65535, gridH: 65535, cellPx: 1, metaH: 6, innerW: 320, innerH: 360, ecc: 0);
        Assert.Null(Layout.UnpackMetadata(strip));
    }

    [Fact]
    public void UnpackMetadata_InconsistentInnerRect_IsRejected()
    {
        // Grid dimensions are in range but the declared inner rectangle does not match the grid —
        // an internally incoherent strip that the encoder can never produce.
        var strip = CraftStrip(bits: 4, gridW: 100, gridH: 100, cellPx: 3, metaH: 10, innerW: 999, innerH: 360, ecc: 16);
        Assert.Null(Layout.UnpackMetadata(strip));
    }

    // ---- Finding: unclamped pixel read crashes on a border-touching frame candidate ----

    [Fact]
    public void InnerRectScanner_BorderTouchingColumnWithNoCrossing_FailsCleanly()
    {
        // A ring with a full-height black crossbar at the center column. Horizontal edge scans
        // resolve on the ring's left/right edges, so the scanner reaches the vertical scans; the
        // crossbar column (a sampled x) is dark top-to-bottom, so its vertical walk probes one
        // pixel past the top/bottom border. Before the fix that was an IndexOutOfRangeException
        // (unclamped Bitmap.At); it must now be a clean ShardDecodeException.
        const int n = 80;
        var px = new Rgb24[n * n]; // black by default
        for (int y = 1; y < n - 1; y++)
            for (int x = 1; x < n - 1; x++)
                if (x != 40) // leave a black vertical crossbar at the sampled column x=40
                    px[y * n + x] = new Rgb24(255, 255, 255);
        var bmp = new Bitmap(px, n, n);
        var frame = new PixelRect(0, 0, n, n); // touches all four image borders

        var ex = Record.Exception(() => new InnerRectScanner().FindInnerRect(bmp, frame));
        Assert.IsType<ShardDecodeException>(ex);
    }

    // ---- Finding: FastPngReader bounds the pixel product but not per-row byte buffers ----

    [Fact]
    public void FastPngReader_ExtremeAspectRatio_RejectedWithoutAllocating()
    {
        using var tmp = new TempDir();
        string path = tmp.File("wide.png");
        File.WriteAllBytes(path, BuildIhdrOnlyPng(width: 500_000_000, height: 1));

        // Must return false (fall back to ImageSharp) rather than allocate ~5 GB of row buffers.
        Assert.False(new FastPngReader().TryRead(path, new DecodeScratch(), out _));
    }

    private static byte[] BuildIhdrOnlyPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // signature
        Span<byte> len = [0, 0, 0, 13];
        ms.Write(len);
        ms.Write("IHDR"u8);
        Span<byte> wh = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(wh, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(wh[4..], height);
        ms.Write(wh);
        ms.Write([8, 6, 0, 0, 0]); // bitDepth=8, colorType=6 (RGBA), comp/filter/interlace=0
        ms.Write([0, 0, 0, 0]);    // chunk CRC (FastPngReader seeks past it; value irrelevant)
        return ms.ToArray();       // the width guard rejects before any IDAT is needed
    }

    // ---- Finding: parallel producer fault surfaces as AggregateException, not the typed error ----

    private sealed class ThrowingFrameSource : IFrameSource
    {
        public IEnumerable<Bitmap> Frames(string path, double fps)
        {
            if (fps > 0)
                throw new ShardDecodeException("producer boom");
            yield break; // makes this a deferred iterator: the throw fires on first MoveNext (in the producer task)
        }
    }

    [Fact]
    public void ParallelDecode_ProducerFault_SurfacesTypedException_NotAggregate()
    {
        using var tmp = new TempDir();
        var decoder = new VideoDecoder(new ShardDecoder(), new ThrowingFrameSource(),
            new ShardAssembler(), new ParityReassembler(), new CameraRectifier());

        // decodeWorkers >= 2 selects the pipelined path where the producer runs on its own task.
        var ex = Record.Exception(() =>
            decoder.Decode("crash.bin", tmp.File("out.bin"), 8, _ => { }, out _, null, decodeWorkers: 2));
        var typed = Assert.IsType<ShardDecodeException>(ex); // not AggregateException
        Assert.Contains("producer boom", typed.Message);
    }

    // ---- Finding: HTML slideshow embeds non-browser formats with an unrenderable MIME ----

    [Fact]
    public void Slideshow_NonBrowserFormat_IsTranscodedToPng()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(4_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 4, BitsPerCell = 2, ImageFormat = "qoi" });
        Assert.All(result.Files, f => Assert.EndsWith(".qoi", f));

        string page = new SlideshowWriter().Write(tmp.File("shards"), result.Files, 400);
        string html = File.ReadAllText(page);
        Assert.Contains("data:image/png;base64,", html);          // qoi frames transcoded to renderable PNG
        Assert.DoesNotContain("application/octet-stream", html);  // never embed an undecodable MIME
    }

    // ---- Finding: public decode API leaks raw file-IO exceptions ----

    [Fact]
    public void Session_AddMissingFile_ReturnsErrorResult_NotThrow()
    {
        var session = new QrShardDecodeSession();
        var result = session.AddImage(Path.Combine(Path.GetTempPath(), "qrshard-nope-" + Guid.NewGuid().ToString("N") + ".png"));
        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Codec_DecodeMissingFile_ThrowsTypedException()
    {
        string missing = Path.Combine(Path.GetTempPath(), "qrshard-nope-" + Guid.NewGuid().ToString("N") + ".png");
        Assert.Throws<QrShardDecodeException>(() => new QrShardCodec().DecodeImages([missing]));
    }

    [Fact]
    public void Session_AddDirectoryPath_ReturnsErrorResult_NotThrow()
    {
        // A directory (or ACL-denied) path makes the FastPngReader FileStream throw
        // UnauthorizedAccessException *before* the ImageSharp load — it must still be translated
        // to the typed error result, not leak. (Missing-file goes via IOException; this is the
        // distinct directory/permission path the first exception-translation pass missed.)
        using var tmp = new TempDir();
        var session = new QrShardDecodeSession();
        var result = session.AddImage(tmp.Sub("a-directory-not-an-image"));
        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Codec_DecodeDirectoryPath_ThrowsTypedException()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("a-directory-not-an-image");
        Assert.Throws<QrShardDecodeException>(() => new QrShardCodec().DecodeImages([dir]));
    }

    // ---- Finding: encode invokes the progress callback concurrently from parallel workers ----

    [Fact]
    public void Encode_ProgressCallback_IsInvokedSerially_NotConcurrently()
    {
        // The encoder renders images on parallel workers and reports progress via a caller
        // callback. If that callback were invoked concurrently, a non-thread-safe sink (a
        // StringWriter — as the CLI test harness uses — or a List) races and can throw
        // intermittently (it did, on the slower/weaker-ordered arm64 release runner). The
        // callback must be serialized. This detects any re-entrant (concurrent) invocation.
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(400_000));

        int active = 0, reentrancy = 0, calls = 0;
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 700, Height = 700, CellPx = 2, BitsPerCell = 4 },
            _ =>
            {
                if (Interlocked.Increment(ref active) != 1)
                    Interlocked.Increment(ref reentrancy);
                Interlocked.Increment(ref calls);
                Thread.Sleep(25); // hold longer than a worker's render, so a racing worker overlaps
                Interlocked.Decrement(ref active);
            });

        Assert.True(result.ImageCount >= 4, $"test needs several images for real concurrency; got {result.ImageCount}");
        Assert.Equal(0, reentrancy);              // never invoked while another invocation is in flight
        Assert.Equal(result.ImageCount, calls);   // one message per image, none lost to a race
    }
}
