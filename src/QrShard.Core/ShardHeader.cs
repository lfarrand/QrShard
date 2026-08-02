using System.Text;
using System.Globalization;

namespace QrShard;

/// <summary>
/// Per-image header carried at the front of every shard's data stream.
/// Every field needed for reassembly is repeated in every image, so any subset of
/// captures can be validated independently and missing shards identified precisely.
///
/// Cross-shard parity (see <see cref="CrossShardFec"/>) adds parity images whose header sets
/// <see cref="FlagParity"/>. For a data image, <see cref="Index"/> is 0..Count-1. For a parity
/// image, it is the global parity ordinal 0..(stripes*StripeParity-1). Stripe geometry
/// (<see cref="StripeData"/>, <see cref="StripeParity"/>) is repeated in every image so any
/// survivor reveals the whole recovery layout.
/// </summary>
internal sealed class ShardHeader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static readonly byte[] Magic = "QRS1"u8.ToArray();
    public const byte FlagCompressed = 0x01;  // payload is compressed (deflate unless FlagBrotli)
    public const byte FlagParity = 0x02;      // this image is cross-shard parity, not data
    public const byte FlagBrotli = 0x04;      // compression algorithm is Brotli (with FlagCompressed)
    public const byte FlagEncrypted = 0x08;   // payload is AES-256-GCM encrypted (password required)
    public const byte FlagArchive = 0x10;     // payload is a tar archive of a folder
    public const byte FlagFountain = 0x20;    // parity images are random-linear fountain frames
    public const byte FlagAuthMeta = 0x40;    // encryption binds the identity header as AES-GCM AAD
    public const byte FlagAuthMetaV2 = 0x80;  // AAD also binds transformation/archive semantics
    public const byte KnownFlags = 0xFF;      // every flag this build understands
    private const byte HeaderVersion = 2;

    // Sanity ceilings for a crafted/corrupt header. The real encoder never approaches these:
    // even the sparsest camera profile packs >1 KB per image, so the 1.5 GB file cap yields far
    // fewer than MaxImages data images. They exist only so the geometry fields cannot drive an
    // integer overflow or an absurd allocation in the reassembler (see the Deserialize guards).
    internal const int MaxImages = 5_000_000;             // ceiling on Count
    internal const long MaxParityOrdinals = 100_000_000;  // ceiling on stripes*StripeParity (fountain 10x headroom)

    public required ulong FileId { get; init; }
    public required int Index { get; init; }
    public required int Count { get; init; }             // number of DATA images
    public required int PayloadLength { get; init; }
    public required uint PayloadCrc32 { get; init; }
    public required long TotalLength { get; init; }      // length of the (possibly compressed) stream that was split
    public required long OriginalLength { get; init; }   // length of the original file
    public required byte Flags { get; init; }
    public required byte[] Sha256 { get; init; }         // hash of the original file
    public required string FileName { get; init; }
    public int StripeData { get; init; }                 // data images per stripe (0 = no cross-shard parity)
    public int StripeParity { get; init; }               // parity images per stripe

    public bool IsParity => (Flags & FlagParity) != 0;

    /// <summary>
    /// Whether two shards repeat the same file identity and recovery geometry. Every shard in a
    /// family carries these fields; only the parity marker, ordinal, payload length and CRC are
    /// expected to differ. Checking them prevents one colliding/poisoned shard from selecting a
    /// different transform, output name, hash, length, or FEC layout for otherwise valid chunks.
    /// </summary>
    internal bool HasSameFamilyAs(ShardHeader other) =>
        FileId == other.FileId &&
        Count == other.Count &&
        TotalLength == other.TotalLength &&
        OriginalLength == other.OriginalLength &&
        ((Flags ^ other.Flags) & ~FlagParity) == 0 &&
        Sha256.AsSpan().SequenceEqual(other.Sha256) &&
        string.Equals(FileName, other.FileName, StringComparison.Ordinal) &&
        StripeData == other.StripeData &&
        StripeParity == other.StripeParity;

    public static int Size(string fileName) => 92 + Encoding.UTF8.GetByteCount(fileName);

    /// <summary>Longest file name rendered in a message before it is truncated.</summary>
    private const int MaxDisplayLength = 120;

    /// <summary>
    /// The header file name, made safe to print. Deserialize accepts up to 4096 bytes of arbitrary
    /// UTF-8 here with no character filtering — only a CRC the crafter also controls — and the
    /// codebase already treats the field as hostile before it reaches the FILESYSTEM
    /// (ShardAssembler.SafeFileName). Nothing treated it as hostile before it reached the TERMINAL,
    /// where it is interpolated into progress lines, `info` output, success logs and every decode
    /// exception message.
    ///
    /// A terminal is an interpreter. Escape sequences in this field can rewrite earlier output,
    /// reposition the cursor, or set the window title — so a crafted shard could forge a
    /// "SHA-256 verified" line, or hide its own presence from a `verify` listing. Carriage returns
    /// alone are enough to overwrite the line just printed. Control characters become '?', and the
    /// result is length-capped so a 4 KB name cannot flood the output either.
    /// </summary>
    public static string Display(string fileName)
        => TerminalText(fileName, MaxDisplayLength);

    /// <summary>Bounds text and neutralizes Unicode characters with terminal direction/layout
    /// semantics. Valid supplementary characters remain intact; isolated surrogate code units do
    /// not. JSON uses its encoder instead and must not pass through this lossy display helper.</summary>
    internal static string TerminalText(string text, int maxLength)
    {
        int keep = Math.Min(text.Length, maxLength);
        var sb = new StringBuilder(keep + 3);
        for (int i = 0; i < keep; i++)
        {
            char c = text[i];
            UnicodeCategory category;
            bool pair = char.IsHighSurrogate(c) && i + 1 < keep && char.IsLowSurrogate(text[i + 1]);
            if (pair)
                category = Rune.GetUnicodeCategory(new Rune(c, text[i + 1]));
            else if (char.IsSurrogate(c))
                category = UnicodeCategory.Surrogate;
            else
                category = char.GetUnicodeCategory(c);

            // Format includes bidi embeddings/overrides/isolates and direction marks. Line and
            // paragraph separators are terminal line controls even though they are outside C0/C1.
            bool unsafeCategory = category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or
                UnicodeCategory.Surrogate;
            if (unsafeCategory)
                sb.Append('?');
            else if (pair)
            {
                sb.Append(c);
                sb.Append(text[++i]);
            }
            else
                sb.Append(c);
        }
        if (text.Length > keep)
            sb.Append("...");
        return sb.ToString();
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(HeaderVersion);
        w.Write(Flags);
        w.Write(FileId);
        w.Write(Index);
        w.Write(Count);
        w.Write(PayloadLength);
        w.Write(PayloadCrc32);
        w.Write(TotalLength);
        w.Write(OriginalLength);
        w.Write(StripeData);
        w.Write(StripeParity);
        w.Write(Sha256);
        byte[] name = Encoding.UTF8.GetBytes(FileName);
        w.Write((ushort)name.Length);
        w.Write(name);
        w.Flush();
        byte[] body = ms.ToArray();
        w.Write(new Crc().Crc32(body));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Parses and validates a header from the front of a decoded stream. Returns null if invalid.</summary>
    public static ShardHeader? Deserialize(byte[] stream, out int headerLength)
    {
        headerLength = 0;
        if (stream.Length < 92 || !stream.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        try
        {
            using var ms = new MemoryStream(stream);
            using var r = new BinaryReader(ms);
            r.ReadBytes(4);
            byte version = r.ReadByte();
            if (version != HeaderVersion)
                return null;
            byte flags = r.ReadByte();
            // Enforce the normative flag mask here, not only in the bitmap decoder. Session files
            // and multi-capture fusion deserialize headers directly and must not silently accept a
            // future feature that the assembler will ignore.
            if ((flags & ~KnownFlags) != 0)
                return null;
            // AuthMeta changes AES-GCM verification and is meaningless without encryption. The
            // wire specification requires this combination to be refused rather than silently
            // treating a malformed/forged flag as harmless decoration.
            if ((flags & FlagAuthMeta) != 0 && (flags & FlagEncrypted) == 0)
                return null;
            if ((flags & FlagAuthMetaV2) != 0 &&
                (flags & (FlagEncrypted | FlagAuthMeta)) != (FlagEncrypted | FlagAuthMeta))
                return null;
            ulong fileId = r.ReadUInt64();
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int payloadLength = r.ReadInt32();
            uint payloadCrc = r.ReadUInt32();
            long totalLength = r.ReadInt64();
            long originalLength = r.ReadInt64();
            int stripeData = r.ReadInt32();
            int stripeParity = r.ReadInt32();
            byte[] sha = r.ReadBytes(32);
            int nameLen = r.ReadUInt16();
            if (nameLen > 4096 || ms.Position + nameLen + 4 > stream.Length)
                return null;
            // The filename bytes are also part of encrypted shards' AAD. Replacement-fallback
            // decoding is not canonical: distinct malformed byte strings can decode to U+FFFD
            // and then authenticate after BuildAad re-encodes a different byte sequence.
            string name = StrictUtf8.GetString(r.ReadBytes(nameLen));
            int bodyLen = (int)ms.Position;
            uint headerCrc = r.ReadUInt32();
            if (headerCrc != new Crc().Crc32(stream.AsSpan(0, bodyLen)))
                return null;

            bool isParity = (flags & FlagParity) != 0;
            // Note: payloadLength is NOT bounded against stream.Length here — Deserialize is
            // legitimately called on a header-only buffer (the payload lives in the full cell
            // stream, validated at the consumer sites). Those guards use `(long)headerLen +
            // PayloadLength` so a crafted PayloadLength = int.MaxValue can no longer overflow the
            // comparison and slip past into the slice; it is rejected cleanly instead.
            if (count < 1 || count > MaxImages || payloadLength < 0 || stripeData < 0 || stripeParity < 0)
                return null;
            if ((stripeData == 0) != (stripeParity == 0))
                return null; // recovery geometry is either wholly absent or wholly specified
            if (index < 0)
                return null;
            if (!isParity && index >= count)
                return null; // data ordinal must fall within the data range
            // Cross-shard coding: bound the whole stripe geometry at this single choke point every
            // header passes through, so no downstream math can divide by zero, overflow int, or
            // allocate an absurd array from crafted fields.
            //   * stripeData is a divisor and a stripe width — must be positive and no larger than a
            //     stripe can hold.
            //   * stripes*StripeParity is the parity ordinal space: an array dimension (new bool[…])
            //     and the index range for parity images. Computed in long here so a crafted
            //     Count/StripeParity pair (which overflowed int before) is rejected, not crashed.
            if (stripeParity > 0)
            {
                if (stripeData < 1 || stripeData > CrossShardFec.MaxShardsPerStripe)
                    return null;
                // CAUCHY ONLY. Its parity rows and data columns index one shared GF(2^8) domain, so
                // their sum is what has to fit: over 255 the byte casts in ParityMatrix wrap until
                // a parity row collides with a data column, x ^ y is zero, and Inv(0) throws
                // DivideByZeroException out of the decode. CrossShardFec.Encode enforces it;
                // TryReconstruct does not, and that is the side a crafted header reaches.
                //
                // Fountain has no such domain. Its coefficients are random over GF(2^8) rather
                // than positions in it, ReassembleFountain branches before any Cauchy matrix is
                // built, StripePlanner.PlanFountain says so in as many words, and SPEC section 8
                // imposes no limit where section 7 does. Applying the Cauchy bound to it made the
                // encoder emit headers its own decoder refused: `-F 299` or higher over a full
                // 64-image fountain stripe put the sum past 255, encode succeeded, and every
                // image — data ones included — then failed with "Shard header is corrupt.
                // Recapture this image.", pointing the user at their camera instead of the flag.
                // The advertised range goes to 1000.
                if ((flags & FlagFountain) != 0)
                {
                    // Fountain rank/inversion is quadratic/cubic in stripe width. The reference
                    // profile fixes that width at no more than min(count, 64); accepting a crafted
                    // 255-wide header here bypassed the planner and multiplied decoder work.
                    if (stripeData > Math.Min(count, FountainFec.MaxStripeData))
                        return null;
                }
                else if (stripeData + (long)stripeParity > CrossShardFec.MaxShardsPerStripe)
                {
                    return null;
                }
                long stripes = ((long)count + stripeData - 1) / stripeData;
                if (stripes * (long)stripeParity > MaxParityOrdinals)
                    return null;
            }

            headerLength = bodyLen + 4;
            return new ShardHeader
            {
                FileId = fileId,
                Index = index,
                Count = count,
                PayloadLength = payloadLength,
                PayloadCrc32 = payloadCrc,
                TotalLength = totalLength,
                OriginalLength = originalLength,
                Flags = flags,
                Sha256 = sha,
                FileName = name,
                StripeData = stripeData,
                StripeParity = stripeParity,
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException)
        {
            return null;
        }
    }
}
