namespace QrShard;

/// <summary>
/// Pixel geometry of a shard image, in encode-space coordinates.
///
/// Structure (outside-in):
///   - white quiet zone (QuietPx)
///   - solid black locator frame (FramePx) — the decoder finds this ring in a screenshot
///   - inner area (InnerW x InnerH), white background, containing:
///       - gap ring of Gutter px (white, so frame scanning terminates cleanly)
///       - top:    metadata strip (128 b/w modules), then palette calibration strip
///       - data grid: GridW x GridH cells of CellPx px, each cell encoding BitsPerCell bits
///       - bottom: palette calibration strip, then metadata strip (redundant copies, so a
///         banner or overlay covering one edge of the capture cannot brick the image)
///
/// All inner proportions are stored in the metadata strip, so the decoder reconstructs exact
/// geometry after reading it; only the strip itself is located by the approximation
/// Gutter ≈ MetaH ≈ innerWidth/100, which both sides compute the same way.
/// </summary>
internal sealed class Layout
{
    public const int QuietPx = 12;
    public const int FramePx = 16;
    public const int Border = QuietPx + FramePx;
    public const int MetaModuleCount = 128;
    public const int MinResolution = 700;
    public const int MaxResolution = 16384;
    public const int MaxCellPx = 64;

    // ---- Camera profile: finder-pattern geometry, in finder modules ----
    // A finder is the classic 7-module concentric square (3-module solid core, 1-module white
    // ring, 1-module black ring) whose row/column signature is 1:1:3:1:1. Four of them sit at
    // the corners of added top/bottom bands, inset 2 modules from the image corner (center at
    // 5.5 modules), plus a solid 3-module orientation tick 7 modules right of the top-left
    // finder center. The camera decoder relies only on these module-relative offsets.
    public const int FinderModules = 7;
    public const int FinderCornerInsetModules = 2;
    public const int FinderBandModules = FinderModules + 2 * FinderCornerInsetModules; // 11
    public const int OrientationTickOffsetModules = 7;

    public required int BitsPerCell { get; init; }
    public required int CellPx { get; init; }
    public required int GridW { get; init; }
    public required int GridH { get; init; }
    public required int MetaH { get; init; }   // also the gutter width
    public required int InnerW { get; init; }
    public required int InnerH { get; init; }
    public required int EccParity { get; init; } // RS parity symbols per 255-byte codeword, 0 = none
    public required int FinderModule { get; init; } // finder module px; 0 = screenshot profile (no bands)

    public bool CameraFinders => FinderModule > 0;
    public int FinderBand => FinderModule * FinderBandModules;

    /// <summary>Rows the frame + inner content are shifted down by (the top finder band).</summary>
    public int ContentTop => FinderBand;

    public int Gutter => MetaH;
    public int Width => InnerW + 2 * Border;
    public int Height => InnerH + 2 * Border + 2 * FinderBand;
    public int DataLeft => Gutter;
    public int DataTop => Gutter + 2 * MetaH;
    public long TotalBits => (long)GridW * GridH * BitsPerCell;
    public long TotalBytes => TotalBits / 8;

    /// <summary>Number of 255-byte RS codewords the cell stream can hold (when ECC is enabled).</summary>
    public int CodewordCount => (int)(TotalBytes / Fec.CodewordLength);

    /// <summary>Bytes available for header + payload after ECC overhead.</summary>
    public long UsableBytes => EccParity == 0 ? TotalBytes : (long)CodewordCount * Fec.DataLength(EccParity);

    public static Layout Create(int width, int height, int cellPx, int bitsPerCell, int eccParity, bool cameraFinders = false)
    {
        if (width is < MinResolution or > MaxResolution || height is < MinResolution or > MaxResolution)
            throw new ArgumentException($"Resolution must be between {MinResolution} and {MaxResolution} in both dimensions.");
        if (cellPx is < 1 or > MaxCellPx)
            throw new ArgumentException($"Cell size must be between 1 and {MaxCellPx} px.");
        if (bitsPerCell is < Palette.MinBits or > Palette.MaxBits)
            throw new ArgumentException($"Bits per cell must be between {Palette.MinBits} and {Palette.MaxBits}.");
        if (eccParity is < 0 or > Fec.MaxParity || (eccParity & 1) != 0)
            throw new ArgumentException($"ECC parity must be an even number between 0 and {Fec.MaxParity}.");

        // Camera profile: reserve top/bottom finder bands within the requested dimensions, so
        // the image still fits the display it will be shown on. Module size scales with the
        // image so finders stay comfortably detectable in a photo.
        int finderModule = 0;
        if (cameraFinders)
        {
            finderModule = Math.Clamp((int)Math.Round(Math.Min(width, height) / 84.0), 8, 48);
            if (height - 2 * finderModule * FinderBandModules < MinResolution / 2)
                throw new ArgumentException("Resolution is too small for the camera profile's finder bands.");
        }
        int band = finderModule * FinderBandModules;

        int innerWTarget = width - 2 * Border;
        int innerHTarget = height - 2 * band - 2 * Border;
        int metaH = EstimateMetaH(innerWTarget);
        int gutter = metaH;

        int gridW = (innerWTarget - 2 * gutter) / cellPx;
        int gridH = (innerHTarget - 2 * gutter - 4 * metaH) / cellPx;
        if (gridW < 16 || gridH < 16)
            throw new ArgumentException("Resolution is too small for the requested cell size.");
        if (gridW > ushort.MaxValue || gridH > ushort.MaxValue)
            throw new ArgumentException("Grid dimensions exceed the encodable maximum.");

        var layout = new Layout
        {
            BitsPerCell = bitsPerCell,
            CellPx = cellPx,
            GridW = gridW,
            GridH = gridH,
            MetaH = metaH,
            InnerW = 2 * gutter + gridW * cellPx,
            InnerH = 2 * gutter + 4 * metaH + gridH * cellPx,
            EccParity = eccParity,
            FinderModule = finderModule,
        };
        if (eccParity > 0 && layout.CodewordCount < 1)
            throw new ArgumentException("Image capacity is too small for error correction; increase resolution or use --ecc 0.");
        return layout;
    }

    /// <summary>Shared encoder/decoder approximation for the metadata strip height and gutter.</summary>
    public static int EstimateMetaH(double innerWidth) => Math.Max(6, (int)Math.Round(innerWidth / 100.0));

    // ---- Metadata strip bit packing (128 modules) ----
    // magic:8 version:4 bitsPerCell:4 gridW:16 gridH:16 cellPx:8 metaH:16 innerW:16 innerH:16 eccParity:8
    //   = 112 bits (14 bytes), then crc16:16 over those 14 bytes = 128.
    public const byte MetaMagic = 0xC5;
    public const int MetaVersion = 2;

    public byte[] PackMetadata()
    {
        var bits = new BitWriter();
        bits.Write(MetaMagic, 8);
        bits.Write(MetaVersion, 4);
        bits.Write((uint)BitsPerCell, 4);
        bits.Write((uint)GridW, 16);
        bits.Write((uint)GridH, 16);
        bits.Write((uint)CellPx, 8);
        bits.Write((uint)MetaH, 16);
        bits.Write((uint)InnerW, 16);
        bits.Write((uint)InnerH, 16);
        bits.Write((uint)EccParity, 8);
        byte[] payload = bits.ToArray(); // 14 bytes
        bits.Write(new Crc().Crc16Ccitt(payload), 16);
        return bits.ToArray(); // 16 bytes = 128 module bits
    }

    public static Layout? UnpackMetadata(ReadOnlySpan<bool> modules)
    {
        if (modules.Length != MetaModuleCount)
            return null;
        var bytes = new byte[16];
        for (int i = 0; i < MetaModuleCount; i++)
            if (modules[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));

        var reader = new BitReader(bytes);
        if (reader.Read(8) != MetaMagic || reader.Read(4) != MetaVersion)
            return null;
        int bitsPerCell = (int)reader.Read(4);
        int gridW = (int)reader.Read(16);
        int gridH = (int)reader.Read(16);
        int cellPx = (int)reader.Read(8);
        int metaH = (int)reader.Read(16);
        int innerW = (int)reader.Read(16);
        int innerH = (int)reader.Read(16);
        int eccParity = (int)reader.Read(8);
        ushort crc = (ushort)reader.Read(16);
        if (crc != new Crc().Crc16Ccitt(bytes.AsSpan(0, 14)))
            return null;
        if (bitsPerCell is < Palette.MinBits or > Palette.MaxBits || gridW < 1 || gridH < 1 || cellPx < 1 || metaH < 1)
            return null;
        if (eccParity is < 0 or > Fec.MaxParity)
            return null;

        return new Layout
        {
            BitsPerCell = bitsPerCell,
            GridW = gridW,
            GridH = gridH,
            CellPx = cellPx,
            MetaH = metaH,
            InnerW = innerW,
            InnerH = innerH,
            EccParity = eccParity,
            // Not carried in the strip: after (any) rectification the decoder maps geometry
            // purely from the frame's inner rectangle, so band info is irrelevant downstream.
            FinderModule = 0,
        };
    }
}
