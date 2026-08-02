using System.IO.Compression;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>Owns the chosen payload source for one encode; disposing releases any file mapping.</summary>
internal sealed class PayloadHandle(IPayloadSource source) : IDisposable
{
    public IPayloadSource Source => source;

    public void Dispose() => source.Dispose();
}

/// <summary>Chooses how the input file is exposed to the encoder and computes its digest.</summary>
internal interface IPayloadPreparer
{
    PayloadHandle Open(string filePath, long length, bool compress, string? password, AppSettings cfg,
        byte semanticFlags, out byte flags, out byte[] sha);

    bool LooksCompressible(IPayloadSource source);
}

/// <summary>
/// Chooses how the input is exposed to the encoder:
///  - empty file → trivial in-memory source;
///  - compressible content (per a mid-file sample for large files) → Brotli-compressed in
///    memory, when that actually wins;
///  - everything else → a memory-mapped source, so large incompressible files (zips, media)
///    are streamed per-chunk and never materialized as a managed array;
///  - a password additionally AES-256-GCM encrypts the (possibly compressed) payload — this
///    path has to materialize the payload in memory, because GCM authenticates the whole
///    message, but it is read into the cipher blob and sealed there, so it materializes once.
/// The header SHA-256 is always the hash of the ORIGINAL file, so verification happens after
/// decrypt + decompress on the receiving side.
/// </summary>
internal sealed class PayloadPreparer(PayloadCipher cipher,
    Action<byte[]>? plaintextClearedObserver = null,
    Func<string, IPayloadSource>? mappedSourceFactory = null,
    Func<IPayloadSource, byte[]>? sha256Factory = null) : IPayloadPreparer
{
    public PayloadPreparer() : this(new PayloadCipher(), null, null, null)
    {
    }

    public PayloadHandle Open(string filePath, long length, bool compress, string? password, AppSettings cfg,
        byte semanticFlags, out byte flags, out byte[] sha)
    {
        flags = (byte)(semanticFlags & (ShardHeader.FlagArchive | ShardHeader.FlagFountain));
        if (length == 0)
        {
            sha = SHA256.HashData([]);
            byte[] empty = [];
            if (password is not null)
            {
                EnsureEncryptedPayloadFitsProtocol(0);
                EnsureEncryptedPayloadFitsManagedArray(0);
                flags |= ShardHeader.FlagEncrypted | ShardHeader.FlagAuthMeta | ShardHeader.FlagAuthMetaV2;
                empty = cipher.Encrypt(empty, password,
                    PayloadCipher.BuildAadV2(0, sha, Path.GetFileName(filePath), flags));
            }
            return new PayloadHandle(new BytePayloadSource(empty));
        }

        IPayloadSource mapped = (mappedSourceFactory ??
            (static path => new MappedPayloadSource(path)))(filePath);
        byte[]? material = null;
        try
        {
            // Hashing reads the mapping and can fail independently (I/O/provider failure, or an
            // injected embedding source). Establish ownership before that first read so no failure
            // path strands a mapped view or file handle.
            sha = (sha256Factory ?? PayloadSource.ComputeSha256)(mapped);
            if (compress && CompressionMaterializationFitsBudget(length, cfg.EncodeMemoryBudgetMB) &&
                LooksCompressible(mapped))
            {
                var original = new byte[checked((int)length)];
                byte[]? compressed = null;
                try
                {
                    mapped.Read(0, original);
                    compressed = Compress(original, cfg.PayloadCompressionLevel);
                    if (compressed.Length < original.Length)
                    {
                        material = compressed;
                        compressed = null; // ownership transferred; clear after encryption or return it
                        flags |= ShardHeader.FlagCompressed | ShardHeader.FlagBrotli;
                    }
                }
                finally
                {
                    ClearPlaintext(original);
                    if (compressed is not null)
                        ClearPlaintext(compressed); // compression lost or threw after allocating output
                }
            }

            if (password is not null)
            {
                flags |= ShardHeader.FlagEncrypted | ShardHeader.FlagAuthMeta | ShardHeader.FlagAuthMetaV2;
                var aad = PayloadCipher.BuildAadV2(length, sha, Path.GetFileName(filePath), flags);
                if (material is null)
                {
                    EnsureEncryptedPayloadFitsProtocol(length);
                    EnsureEncryptedPayloadFitsManagedArray(length);
                    EnsureEncryptionFitsBudget(length, cfg.EncodeMemoryBudgetMB);
                    // Read the file straight into the blob's body and seal it there: the plaintext and
                    // the ciphertext are the same bytes, so an incompressible input is materialized
                    // once rather than twice.
                    byte[] blob = PayloadCipher.AllocateBlob(length);
                    bool sealedSuccessfully = false;
                    try
                    {
                        mapped.Read(0, PayloadCipher.Body(blob));
                        cipher.SealInPlace(blob, password, aad);
                        sealedSuccessfully = true;
                        material = blob;
                    }
                    finally
                    {
                        if (!sealedSuccessfully)
                            ClearPlaintext(blob);
                    }
                }
                else
                {
                    byte[] plaintext = material;
                    material = null; // plaintext is owned by the finally until ciphertext exists
                    try
                    {
                        EnsureEncryptedPayloadFitsProtocol(plaintext.LongLength);
                        EnsureEncryptedPayloadFitsManagedArray(plaintext.LongLength);
                        EnsureEncryptionCopyFitsBudget(plaintext.LongLength, cfg.EncodeMemoryBudgetMB);
                        material = cipher.Encrypt(plaintext, password, aad);
                    }
                    finally
                    {
                        ClearPlaintext(plaintext);
                    }
                }
            }

            if (material is not null)
            {
                mapped.Dispose();
                return new PayloadHandle(new BytePayloadSource(material));
            }
            return new PayloadHandle(mapped);
        }
        catch
        {
            if (material is not null)
                ClearPlaintext(material);
            mapped.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Brotli currently needs the original array, a growing MemoryStream (less than twice input
    /// in the worst useful case), and ToArray's result at once. Four input lengths is the safe
    /// upper bound; if it does not fit, compression is an optional optimization and is skipped.
    /// </summary>
    internal static bool CompressionMaterializationFitsBudget(long length, int budgetMB)
    {
        if (length < 0 || length > Array.MaxLength)
            return false;
        try
        {
            return checked(length * 4) <= checked(budgetMB * 1_000_000L);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static void EnsureEncryptionFitsBudget(long length, int budgetMB)
    {
        long needed = checked(length + PayloadCipher.Overhead);
        long budget = checked(budgetMB * 1_000_000L);
        if (needed > budget)
            throw new InvalidOperationException(
                $"Password encryption needs ~{needed / 1_000_000:N0} MB for its authenticated payload, " +
                $"above EncodeMemoryBudgetMB={budgetMB:N0}. Raise the budget deliberately or split the input.");
    }

    /// <summary>Reject AES-GCM's 44-byte envelope before allocating a protocol-oversized blob.</summary>
    internal static void EnsureEncryptedPayloadFitsProtocol(long plaintextLength)
    {
        long preparedLength;
        try
        {
            preparedLength = checked(plaintextLength + PayloadCipher.Overhead);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("The encrypted payload length exceeds the supported protocol limit.");
        }
        ShardEncoder.EnsurePreparedLengthSupported(preparedLength);
    }

    /// <summary>
    /// AES-GCM is intentionally one authenticated payload, so the current implementation must
    /// materialize its envelope in one SZ array. Refuse the runtime-impossible case before a large
    /// allocation attempt; unencrypted payloads remain streamable up to the wire-format limit.
    /// </summary>
    internal static void EnsureEncryptedPayloadFitsManagedArray(long plaintextLength)
    {
        long maximumPlaintext = (long)Array.MaxLength - PayloadCipher.Overhead;
        if (plaintextLength < 0 || plaintextLength > maximumPlaintext)
            throw new InvalidOperationException(
                "Password encryption requires one contiguous managed payload; split this input into smaller transfers.");
    }

    private static void EnsureEncryptionCopyFitsBudget(long length, int budgetMB)
    {
        long needed = checked(length * 2 + PayloadCipher.Overhead);
        long budget = checked(budgetMB * 1_000_000L);
        if (needed > budget)
            throw new InvalidOperationException(
                $"Encrypting the compressed payload needs ~{needed / 1_000_000:N0} MB transiently, " +
                $"above EncodeMemoryBudgetMB={budgetMB:N0}. Raise the budget deliberately or use --no-compress.");
    }

    /// <summary>
    /// Cheap pre-check before compressing large inputs: deflating a mid-file sample at the
    /// fastest level tells us whether a full pass is worth the CPU (a .zip/.mp4 is not).
    /// </summary>
    public bool LooksCompressible(IPayloadSource source)
    {
        const int threshold = 4_000_000, sampleLen = 1_000_000;
        if (source.Length <= threshold)
            return true;
        var sample = new byte[sampleLen];
        MemoryStream? ms = null;
        try
        {
            source.Read(source.Length / 2 - sampleLen / 2, sample);
            ms = new MemoryStream();
            using (var probe = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                probe.Write(sample);
            return ms.Length < sampleLen * 98L / 100;
        }
        finally
        {
            ClearPlaintext(sample);
            if (ms is not null && ms.TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array is not null)
                ClearPlaintext(buffer.Array);
            ms?.Dispose();
        }
    }

    private byte[] Compress(byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream();
        try
        {
            using (var brotli = new BrotliStream(ms, level, leaveOpen: true))
                brotli.Write(data);
            return ms.ToArray();
        }
        finally
        {
            if (ms.TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array is not null)
                ClearPlaintext(buffer.Array);
            ms.Dispose();
        }
    }

    private void ClearPlaintext(byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        plaintextClearedObserver?.Invoke(buffer);
    }
}
