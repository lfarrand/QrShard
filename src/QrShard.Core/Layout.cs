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

    /// <summary>Largest value a 14-bit version-4 metadata field can carry.</summary>
    public const int MaxMetaField = (1 << 14) - 1;

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
    public bool Interleave2 { get; init; } // v2 interleave: seeded permutation of the ECC byte layout

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

    public static Layout Create(int width, int height, int cellPx, int bitsPerCell, int eccParity,
        bool cameraFinders = false, bool interleave2 = false)
    {
        if (interleave2 && eccParity == 0)
            throw new ArgumentException("Interleave v2 permutes the ECC byte layout and needs ECC enabled.");
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
        // Version 4 narrowed these fields from 16 bits to 14; the guard kept naming ushort, a
        // bound four times too loose for the format actually being written. Nothing can reach
        // either today — Border 28 and metaH = innerW/100 cap gridW near 16002 at MaxResolution
        // with cellPx 1 — but BitWriter.Write truncates silently rather than throwing, so if that
        // headroom ever closes the failure is a corrupt strip, not an exception.
        if (gridW > MaxMetaField || gridH > MaxMetaField || metaH > MaxMetaField)
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
            Interleave2 = interleave2,
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
    // The strip is packed full, so new capabilities ride the VERSION nibble: version 2 is the
    // classic modular interleave, version 3 the same fields with the v2 permuted interleave.
    public const byte MetaMagic = 0xC5;
    public const int MetaVersion = 2;
    public const int MetaVersionInterleave2 = 3;

    // ---- Version 4: the same information, error-corrected ----
    //
    // Versions 2 and 3 spend all 128 modules on 112 bits of fields plus a CRC-16, so a single
    // flipped module loses the whole image — before the ECC that protects the data grid is ever
    // consulted. The strips are duplicated top and bottom, but both copies sit at the SAME x as
    // each other, so one narrow vertical mark can take out the same module in both.
    //
    // Room comes from two places. The dimension fields were sized to their storage rather than to
    // their ranges (gridW/gridH/metaH cannot exceed MaxResolution, cellPx cannot exceed 64,
    // eccParity is even and at most 64), and innerW/innerH are pure redundancy: version 2 carries
    // them and then the decoder REJECTS any strip where they disagree with
    // 2*metaH + gridW*cellPx / 6*metaH + gridH*cellPx. Deriving them cannot lose information,
    // because a disagreeing strip was never accepted.
    //
    //   9 bytes  fields, 71 bits used and 1 reserved (zero)
    //   2 bytes  CRC-16/CCITT over those 9
    //   5 bytes  Reed-Solomon parity over the 11 preceding, GF(2^8), the codec already in the box
    //  16 bytes  = 128 modules, unchanged
    //
    // RS with 5 parity symbols corrects 2 symbol errors anywhere in the 16. Symbols are laid out
    // CONTIGUOUSLY and deliberately not interleaved: the damage this exists for is a burst — a
    // mark, a cable, a scratch — and byte alignment is what absorbs a burst. Interleaving would
    // scatter one mark across more symbols, which is precisely the wrong direction here. Two
    // symbols buys roughly 8 adjacent modules, about 130 px at the 2160 px default's 16.1 px
    // module pitch, against the single module that used to be fatal.
    //
    // The CRC is kept rather than trusting the code's own detection. RS can miscorrect beyond its
    // bound, and this project's whole posture is to verify rather than assume: correct first, then
    // check, and reject if the check fails.
    public const int MetaVersionFec = 4;

    /// <summary>Field bytes in a v4 strip, before CRC and parity.</summary>
    private const int V4FieldBytes = 9;

    /// <summary>Field + CRC bytes; the RS message length.</summary>
    private const int V4MessageBytes = 11;

    /// <summary>RS parity symbols, correcting <c>V4ParityBytes / 2</c> symbol errors.</summary>
    private const int V4ParityBytes = 5;

    public byte[] PackMetadata()
    {
        var bits = new BitWriter();
        bits.Write(MetaMagic, 8);
        bits.Write(MetaVersionFec, 4);
        bits.Write((uint)BitsPerCell, 4);
        bits.Write((uint)GridW, 14);
        bits.Write((uint)GridH, 14);
        bits.Write((uint)(CellPx - 1), 6);   // 1..64 stored as 0..63
        bits.Write((uint)MetaH, 14);
        bits.Write((uint)(EccParity / 2), 6); // even 0..64 stored as 0..32
        // The interleave that version 3 signalled is now a field rather than a version, so v4
        // carries both variants and the version nibble stays free for the next capability.
        bits.Write((uint)(Interleave2 ? 1 : 0), 1);
        bits.Write(0, 1);                     // reserved, must be zero — 72 bits exactly
        byte[] fields = bits.ToArray();       // 9 bytes
        bits.Write(new Crc().Crc16Ccitt(fields), 16);

        byte[] message = bits.ToArray();      // 11 bytes: fields + CRC
        var strip = new byte[V4MessageBytes + V4ParityBytes];
        message.CopyTo(strip, 0);
        new ReedSolomon().Encode(message, strip.AsSpan(V4MessageBytes, V4ParityBytes));
        return strip;                         // 16 bytes = 128 module bits
    }

    /// <summary>
    /// Repairs and unpacks a v4 strip. Returns null when the damage exceeds what 5 parity symbols
    /// can correct, or when the CRC still fails afterwards — a miscorrection RS could not detect.
    /// </summary>
    private static Layout? UnpackV4(byte[] strip)
    {
        // Correct in place first. TryDecode returning false means the damage is past the bound;
        // returning true still has to satisfy the CRC below, because correction beyond the bound
        // can succeed loudly and be wrong.
        if (!new ReedSolomon().TryDecode(strip, V4ParityBytes, out _))
            return null;

        var reader = new BitReader(strip);
        // Both MUST be checked here, and the comments that used to sit on these two lines claimed
        // the caller had already matched them. It has not: UnpackMetadata dispatches to this
        // method precisely BECAUSE the raw bytes did not look like v2 or v3, which is what lets a
        // damaged magic or version be repaired. So the check has to happen after correction, and
        // it was simply missing.
        //
        // The consequence is worse than a lax magic. SPEC section 2.2 requires unknown versions to
        // be rejected — that nibble is the format's capability field, and the whole reason a v4
        // strip is refused by older builds. Without this, a future version 5 strip whose CRC
        // happens to verify would be silently PARSED AS v4 by this decoder: fields read at the
        // wrong offsets, geometry wrong, and no error. Rejecting unknown versions is what makes
        // adding version 5 safe later.
        if (reader.Read(8) != MetaMagic || reader.Read(4) != MetaVersionFec)
            return null;
        int bitsPerCell = (int)reader.Read(4);
        int gridW = (int)reader.Read(14);
        int gridH = (int)reader.Read(14);
        int cellPx = (int)reader.Read(6) + 1;
        int metaH = (int)reader.Read(14);
        int eccParity = (int)reader.Read(6) * 2;
        bool interleave2 = reader.Read(1) == 1;
        if (reader.Read(1) != 0)              // reserved bit must be zero
            return null;
        ushort crc = (ushort)reader.Read(16);
        if (crc != new Crc().Crc16Ccitt(strip.AsSpan(0, V4FieldBytes)))
            return null;

        // Derived, not carried. v2 stored these and rejected any strip where they disagreed with
        // exactly this arithmetic, so computing them is the same constraint expressed once.
        long innerW = 2L * metaH + (long)gridW * cellPx;
        long innerH = 6L * metaH + (long)gridH * cellPx;
        if (innerW is < 1 or > MaxResolution || innerH is < 1 or > MaxResolution)
            return null;

        return Validated(bitsPerCell, gridW, gridH, cellPx, metaH, (int)innerW, (int)innerH, eccParity, interleave2);
    }

    /// <summary>
    /// Rejects a layout whose declared grid is finer than the area it was captured into. A cell
    /// cannot be resolved from less than one pixel, so this can only be a strip that is lying.
    ///
    /// Shared because it was not: GridSampler enforced it, and the diagnostics path allocated
    /// GridW*GridH ints and rendered a GridW*6 x GridH*6 heatmap from the same unvalidated fields
    /// several statements earlier. A 8.8 KB PNG declaring 2000x2000 produced a 12000x12000,
    /// 4.2 MB heatmap on disk before the decoder rejected the very same layout as impossible.
    /// </summary>
    public void RequireResolvableIn(double innerW, double innerH)
    {
        if (GridW > innerW || GridH > innerH)
            throw new ShardDecodeException(
                $"Shard metadata declares a {GridW}x{GridH} grid, finer than the " +
                $"{innerW}x{innerH} area it was found in; the capture cannot resolve it.");
    }

    public static Layout? UnpackMetadata(ReadOnlySpan<bool> modules)
    {
        if (modules.Length != MetaModuleCount)
            return null;
        var bytes = new byte[16];
        for (int i = 0; i < MetaModuleCount; i++)
            if (modules[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));

        // Dispatch on what the UNCORRECTED bytes say, because for v4 the magic and version live
        // inside the RS-protected region: if they are the damaged bits, checking them first would
        // reject the strip before the parity that could repair them ever runs. So an exact v2/v3
        // header takes the legacy path, and anything else is offered to v4, which corrects first
        // and only then insists on the magic. A genuinely corrupt v2 strip fails either way.
        var reader = new BitReader(bytes);
        uint magic = reader.Read(8);
        uint version = reader.Read(4);
        if (magic != MetaMagic || version is not (MetaVersion or MetaVersionInterleave2))
            return UnpackV4(bytes);
        // ...and if the legacy read then FAILS, still offer it to v4. Dispatching on uncorrected
        // bytes is necessary, but it left a gap in exactly the case v4 exists for: a burst landing
        // on byte 1 of a v4 strip can leave the magic intact and turn the version nibble into a 2
        // or a 3, which routed the strip to a parser that cannot check its CRC — and the five
        // parity symbols that would have repaired that nibble never ran. The image was lost to
        // damage the format is specifically built to absorb. A legacy strip that fails its own CRC
        // is corrupt anyway, so trying v4 second costs nothing and can only recover.
        return UnpackLegacy(reader, bytes, version) ?? UnpackV4(bytes);
    }

    private static Layout? UnpackLegacy(BitReader reader, byte[] bytes, uint version)
    {
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
        return Validated(bitsPerCell, gridW, gridH, cellPx, metaH, innerW, innerH, eccParity,
            interleave2: version == MetaVersionInterleave2);
    }

    /// <summary>
    /// The checks every accepted strip must pass, whatever version carried it. Shared so a new
    /// version cannot quietly acquire a weaker decode path than the one before it — which is the
    /// mistake this codebase has already made once, enforcing an encoder invariant only on the
    /// side that is not under attack.
    /// </summary>
    private static Layout? Validated(int bitsPerCell, int gridW, int gridH, int cellPx, int metaH,
        int innerW, int innerH, int eccParity, bool interleave2)
    {
        if (bitsPerCell is < Palette.MinBits or > Palette.MaxBits || gridW < 1 || gridH < 1 || cellPx < 1 || metaH < 1)
            return null;
        // Range alone is not enough: Create rejects ODD parity, and the decode path has to reject
        // it too. Parity 1 in particular makes Fec.TryErasureRetry compute
        // `parity - VerificationMargin` = -1, a limit its `f == limit` bound can never reach, so
        // the erasure list runs past its 64-entry buffer.
        if (eccParity is < 0 or > Fec.MaxParity || (eccParity & 1) != 0)
            return null;
        // Bound the geometry so a strip that survives its checksum cannot drive an overflowing or
        // absurd buffer size downstream — GridSampler sizes
        // `streamLength = (int)((GridW*GridH*BitsPerCell+7)/8)`, which without a cap overflows int
        // to a negative length or demands multi-GB.
        if (gridW > MaxResolution || gridH > MaxResolution || cellPx > MaxCellPx || metaH > MaxResolution
            || innerW is < 1 or > MaxResolution || innerH is < 1 or > MaxResolution)
            return null;
        // v2/v3 carry the inner rectangle and must agree with the grid; v4 derives it from the
        // same arithmetic, so this is a tautology there and a real cross-check here.
        if (innerW != 2 * metaH + gridW * cellPx || innerH != 6 * metaH + gridH * cellPx)
            return null;
        if (interleave2 && eccParity == 0)
            return null; // the permutation is defined over the ECC layout

        var layout = new Layout
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
            Interleave2 = interleave2,
        };
        // With CodewordCount 0 the FEC pass writes nothing and reports success, so the recovered
        // buffer — pooled per worker and never cleared — is handed on still holding the PREVIOUS
        // image's fully valid stream, and a shard is accepted from an image that contributed no
        // bytes to it.
        if (layout.EccParity > 0 && layout.CodewordCount < 1)
            return null;
        return layout;
    }
}
