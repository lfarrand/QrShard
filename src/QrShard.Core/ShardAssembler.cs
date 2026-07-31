using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>
/// Reassembles decoded shards into output files. The chunk sequence is streamed through
/// decrypt/decompress straight to disk with an incremental SHA-256, so peak memory is the chunk
/// buffers themselves and nothing else. Encrypted payloads are the one exception: GCM
/// authenticates the whole message, so the ciphertext has to be gathered into one contiguous
/// buffer before the tag can be checked. That buffer is decrypted in place, which costs one
/// extra copy of the payload rather than two.
/// </summary>
internal sealed class ShardAssembler(IParityReassembler parityReassembler, PayloadCipher cipher) : IShardAssembler
{
    public ShardAssembler() : this(new ParityReassembler(), new PayloadCipher())
    {
    }

    /// <summary>Reassembles already-decoded shards into output file(s). Shared by folder and video decoding.</summary>
    public List<RestoredFile> Assemble(List<DecodedShard> shards, string? outputPath, Action<string> log, string? password = null)
    {
        var groups = shards.GroupBy(s => s.Header.FileId).ToList();
        if (outputPath is not null && groups.Count > 1)
            throw new ShardDecodeException("The images belong to multiple different files; omit -o or decode them separately.");

        var restored = new List<RestoredFile>();
        foreach (var group in groups)
            restored.Add(Reassemble([.. group], outputPath, log, password));
        return restored;
    }

    private RestoredFile Reassemble(List<DecodedShard> shards, string? outputPath, Action<string> log, string? password)
    {
        var first = shards[0].Header;
        int count = first.Count;
        foreach (var s in shards)
            if (s.Header.Count != count)
                throw new ShardDecodeException($"Inconsistent shard set for '{ShardHeader.Display(first.FileName)}': image counts differ.");

        // Both reassembly paths bound their buffers by the declared sizes, so sanity-check first.
        if (first.TotalLength is < 0 or > ShardEncoder.MaxFileBytes || first.OriginalLength is < 0 or > ShardEncoder.MaxFileBytes)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares an implausible file size.");
        // Cross-shard geometry drives divisor/array math in both parity paths. Deserialize
        // already rejects this, but a directly-constructed shard set (session API, tests) must
        // fail cleanly rather than crash.
        if (first.StripeParity > 0 && first.StripeData < 1)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares invalid stripe geometry.");

        byte[][] chunks;
        long[] chunkLengths;
        if (first.StripeParity > 0)
        {
            chunks = parityReassembler.ReassembleWithParity(shards, first, log, out int cap);
            chunkLengths = new long[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
                chunkLengths[i] = Math.Min(cap, first.TotalLength - (long)i * cap);
        }
        else
        {
            chunks = CollectContiguous(shards, first);
            chunkLengths = new long[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
                chunkLengths[i] = chunks[i].Length;
        }

        bool encrypted = (first.Flags & ShardHeader.FlagEncrypted) != 0;
        bool compressed = (first.Flags & ShardHeader.FlagCompressed) != 0;
        bool archive = (first.Flags & ShardHeader.FlagArchive) != 0;

        Stream source = new ChunkConcatStream(chunks, chunkLengths);
        if (encrypted)
        {
            if (password is null)
                throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}' is encrypted; supply the password with -p/--password.");
            var blob = new byte[first.TotalLength];
            source.ReadExactly(blob);
            // Newer encrypted shards bind the identity header as AAD; older ones (no FlagAuthMeta)
            // decrypt with empty AAD, which GCM treats identically to no AAD.
            ReadOnlySpan<byte> aad = (first.Flags & ShardHeader.FlagAuthMeta) != 0
                ? PayloadCipher.BuildAad(first.OriginalLength, first.Sha256, first.FileName)
                : default;
            // Decrypting in place keeps the encrypted path to two live buffers (chunks + blob)
            // rather than three; the tag is still verified over the whole message first.
            var plain = cipher.DecryptInPlace(blob, password, first.FileName, aad);
            source = new MemoryStream(plain.Array!, plain.Offset, plain.Count, writable: false);
        }
        if (compressed)
        {
            source = (first.Flags & ShardHeader.FlagBrotli) != 0
                ? new BrotliStream(source, CompressionMode.Decompress)
                : new DeflateStream(source, CompressionMode.Decompress);
        }

        // Archives restore into a directory; the tar itself is a transient temp file.
        // The intermediate tar for an archive payload is DECRYPTED PLAINTEXT. Naming it from the
        // FileId put it at a fully predictable path in the shared temp root: FileId is a cleartext
        // header field carried in every shard image, so anyone who has seen the images — the whole
        // distribution model of this tool — knows the filename before the victim decodes. On Unix
        // that root is typically /tmp, mode 1777, and FileStream creates with 0666 & ~umask and no
        // O_EXCL, so a pre-planted file or symlink of that exact name is opened rather than
        // refused. A private directory removes both the predictability and the shared-root
        // exposure: CreateTempSubdirectory makes it 0700 on Unix and its name is random.
        string tempDir = archive ? Directory.CreateTempSubdirectory("qrshard-").FullName : "";
        string outPath = archive
            ? Path.Combine(tempDir, "payload.tar")
            : ResolveOutputPath(first, outputPath);

        long written = 0;
        byte[] sha;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 20];
                int n;
                while (written <= first.OriginalLength && (n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    hash.AppendData(buffer, 0, n);
                    written += n;
                }
            }
            sha = hash.GetHashAndReset();
        }
        catch (InvalidDataException)
        {
            TryDelete(outPath);
            TryDeleteDirectory(tempDir);
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': the reassembled stream failed to decompress. A shard is corrupt beyond recovery.");
        }
        finally
        {
            source.Dispose();
        }

        if (written != first.OriginalLength || !sha.AsSpan().SequenceEqual(first.Sha256))
        {
            TryDelete(outPath);
            TryDeleteDirectory(tempDir);
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': SHA-256 of the reassembled file does not match the original. A shard was corrupted.");
        }

        if (archive)
        {
            // Collision avoidance was added to the single-file path and stopped two lines short of
            // this one. The archive branch extracts with overwrite: true, so two groups sharing a
            // header FileName landed in the identical directory and the later one silently
            // replaced the earlier one's files. Not exotic: every multi-input encode is named
            // "bundle", so any two `qrshard encode a b c` sets decoded together collide by default.
            string destDir = outputPath ?? FreeDirectory(SafeDirectoryName(first.FileName));
            try
            {
                ExtractTar(outPath, destDir);
            }
            finally
            {
                TryDelete(outPath);
                TryDeleteDirectory(tempDir);
            }
            log($"  SHA-256 verified ✓  '{ShardHeader.Display(first.FileName)}' → extracted to {destDir}");
            return new RestoredFile(first.FileName, destDir, written);
        }

        log($"  SHA-256 verified ✓  '{ShardHeader.Display(first.FileName)}' → {outPath} ({written:N0} bytes)");
        return new RestoredFile(first.FileName, outPath, written);
    }

    /// <summary>
    /// Manual tar extraction instead of TarFile.ExtractToDirectory: the built-in containment
    /// check compares the destination STRING against symlink-resolved entry paths, so any
    /// destination under a symlinked parent (macOS's /var → /private/var temp dir, notably)
    /// spuriously fails as "outside the destination". Building both sides of our own zip-slip
    /// guard from the same Path.GetFullPath keeps them consistent regardless of symlinks.
    /// </summary>
    private static void ExtractTar(string tarPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string destRoot = Path.GetFullPath(destDir);
        // GetFullPath PRESERVES a trailing separator, so `-o out/` yields "…/out/" and the old
        // `destRoot + separator` prefix became "…/out//" — a doubled separator no normalised
        // target can ever start with, so every entry failed the guard and a perfectly ordinary
        // command looked like a corrupt archive. Filesystem roots ("E:\", "/") carry the
        // separator inherently and failed the same way. Build the prefix so it ends with exactly
        // one separator whichever form arrived, and compare the bare form for the root itself.
        string destBare = Path.TrimEndingDirectorySeparator(destRoot);
        string destPrefix = destBare + Path.DirectorySeparatorChar;
        using var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            string target = Path.GetFullPath(Path.Combine(destBare, entry.Name));
            if (!target.StartsWith(destPrefix, StringComparison.Ordinal) && target != destBare)
                throw new ShardDecodeException($"Archive entry '{entry.Name}' escapes the destination directory.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    break;
                default:
                    break; // links, fifos, pax metadata — our own encoder never writes them
            }
        }
    }

    /// <summary>
    /// Reduces the header's file name to a bare name for path construction. Shard headers come
    /// from untrusted images, and this value is attacker-controlled: "../../x" escapes the
    /// working directory, and an absolute name is worse still, because Path.Combine discards its
    /// first argument entirely when the second is rooted — so the write lands wherever the header
    /// says. Only path construction is sanitized; the original value is still what gets logged
    /// and bound as AES-GCM associated data, so existing shards keep decrypting.
    /// </summary>
    internal static string SafeFileName(string fileName)
    {
        // Split on BOTH separators regardless of host OS. Path.GetFileName alone is
        // platform-dependent — on Linux '\' is an ordinary filename character, so a
        // Windows-style "..\..\x" would survive intact — and shards are explicitly
        // cross-platform, so a name must be neutralised the same way everywhere.
        int cut = fileName.LastIndexOfAny(['/', '\\']);
        string name = cut >= 0 ? fileName[(cut + 1)..] : fileName;

        // Stripping directories leaves "." and ".." intact, and both still traverse.
        if (name.Length == 0 || name is "." or "..")
            return FallbackFileName;

        // Invalid-character sets are also platform-dependent, so reject the union: a name is
        // only accepted if it is a plain file name on every platform. ':' additionally guards
        // the Windows drive-relative form ("C:x") and NTFS alternate data streams.
        foreach (char c in name)
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ')
                return FallbackFileName;

        // Win32 DOS device names are not files. Opening "<dir>\NUL" with FileMode.Create SUCCEEDS,
        // every byte written is discarded, and no file appears — while File.Exists is false, so
        // the collision check below never diverts, and the SHA-256 still matches because it is
        // computed over the source stream rather than read back. A crafted header naming NUL
        // therefore makes the decode report "SHA-256 verified" over a file that does not exist:
        // silent, total data loss dressed as success, which is exactly the outcome the whole
        // verify-don't-assume design exists to prevent. The check is unconditional rather than
        // OS-gated so a name is only accepted if it is a plain file on every platform, matching
        // the character rule above.
        if (IsDosDeviceName(name))
            return FallbackFileName;

        // Windows silently strips trailing dots and spaces, so "evil. " and "evil" name the same
        // file. Left through, two distinct headers collide and the collision check cannot see it.
        if (name[^1] is '.' or ' ')
            return FallbackFileName;

        return name;
    }

    private static readonly string[] DosDevices =
        ["CON", "PRN", "AUX", "NUL", "CLOCK$",
         "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Windows resolves a device name with OR without an extension — "NUL", "NUL.txt" and
    /// "NUL.tar.gz" all reach the null device — so the test is on the stem before the first dot.
    /// </summary>
    private static bool IsDosDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? name : name.AsSpan(0, dot);
        foreach (string device in DosDevices)
            if (stem.Equals(device, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private const string FallbackFileName = "restored.bin";
    private const string FallbackDirectoryName = "restored";

    /// <summary>
    /// The archive counterpart of <see cref="SafeFileName"/>: a directory name for a tar payload,
    /// derived from the same untrusted header field.
    ///
    /// Stripping the extension is what makes this its own function rather than a call to
    /// SafeFileName. "..." is not "." or ".." so it survives that check intact, but
    /// Path.GetFileNameWithoutExtension("...") is "..", which resolves to the PARENT of the
    /// working directory — and the tar extractor then anchors its own containment check to that
    /// already-escaped root and writes with overwrite enabled. So the extension is stripped first
    /// and anything left that is only dots is rejected.
    /// </summary>
    internal static string SafeDirectoryName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(SafeFileName(fileName));
        return stem.Length == 0 || stem.Trim('.').Length == 0 ? FallbackDirectoryName : stem;
    }

    private static string ResolveOutputPath(ShardHeader first, string? outputPath)
    {
        // An explicit -o is the user's own instruction and is used as given; only the name
        // carried inside the image is untrusted.
        if (outputPath is not null)
            return outputPath;

        string safe = SafeFileName(first.FileName);
        string outPath = Path.Combine(Environment.CurrentDirectory, safe);
        if (!File.Exists(outPath))
            return outPath;

        // The fallback used to be returned without a check of its own, so it protected the
        // ORIGINAL file and then clobbered anything already sitting on the fallback name — a
        // previous decode's output, most obviously. Worse, Assemble resolves one group at a time,
        // so three groups sharing a header FileName sent groups 2 and 3 to the same fallback and
        // FileMode.Create truncated group 2's output mid-run: the tool silently losing a file it
        // had just successfully restored. Keep counting until a name is actually free.
        string stem = Path.GetFileNameWithoutExtension(safe);
        string ext = Path.GetExtension(safe);
        for (int n = 1; n < 10_000; n++)
        {
            string candidate = Path.Combine(Environment.CurrentDirectory,
                n == 1 ? $"{stem}.restored{ext}" : $"{stem}.restored-{n}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new ShardDecodeException(
            $"Cannot find a free output name for '{safe}' — 10,000 variants already exist. Pass -o explicitly.");
    }

    /// <summary>
    /// An extraction directory that does not already exist, counting like the single-file path
    /// does. An EMPTY existing directory is reused: `qrshard decode` into a folder the user made
    /// themselves is normal, and diverting to "name.restored" there would be surprising. Only a
    /// directory with something in it is treated as occupied.
    /// </summary>
    private static string FreeDirectory(string stem)
    {
        string first = Path.Combine(Environment.CurrentDirectory, stem);
        if (!Directory.Exists(first) || !Directory.EnumerateFileSystemEntries(first).Any())
            return first;
        for (int n = 1; n < 10_000; n++)
        {
            string candidate = Path.Combine(Environment.CurrentDirectory,
                n == 1 ? $"{stem}.restored" : $"{stem}.restored-{n}");
            if (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any())
                return candidate;
        }
        throw new ShardDecodeException(
            $"Cannot find a free extraction directory for '{stem}' — 10,000 variants are already in use. Pass -o explicitly.");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best effort: a leftover temp directory is untidy, not incorrect, and must never
            // mask the decode result that the caller is actually waiting on.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort — verification already failed louder
        }
    }

    /// <summary>Original path: no cross-shard parity — every data image must be present.</summary>
    private static byte[][] CollectContiguous(List<DecodedShard> shards, ShardHeader first)
    {
        var byIndex = new DecodedShard?[first.Count];
        foreach (var s in shards)
            if (!s.Header.IsParity && (uint)s.Header.Index < (uint)first.Count) // guard crafted out-of-range ordinals
                byIndex[s.Header.Index] ??= s;

        var missing = Enumerable.Range(0, first.Count).Where(i => byIndex[i] is null).ToList();
        if (missing.Count > 0)
            throw new ShardDecodeException(
                $"'{ShardHeader.Display(first.FileName)}': missing image(s) {string.Join(", ", missing.Select(i => i + 1))} of {first.Count}. " +
                "Capture them and decode again.");

        if (byIndex.Sum(s => (long)s!.Payload.Length) != first.TotalLength)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': reassembled length does not match expected {first.TotalLength:N0}.");

        return [.. byIndex.Select(s => s!.Payload)];
    }

    /// <summary>Reads a chunk sequence as one stream, consuming only the declared prefix of each chunk.</summary>
    private sealed class ChunkConcatStream(byte[][] chunks, long[] lengths) : Stream
    {
        private int _index;
        private long _posInChunk;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_index < chunks.Length)
            {
                long remaining = lengths[_index] - _posInChunk;
                if (remaining <= 0)
                {
                    _index++;
                    _posInChunk = 0;
                    continue;
                }
                int n = (int)Math.Min(buffer.Length, remaining);
                chunks[_index].AsSpan((int)_posInChunk, n).CopyTo(buffer);
                _posInChunk += n;
                return n;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
