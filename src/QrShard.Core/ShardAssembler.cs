using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>
/// Reassembles decoded shards into output files. The chunk sequence is streamed through
/// decrypt/decompress straight to disk with an incremental SHA-256, so peak memory is one
/// buffer — not two copies of the whole file (encrypted payloads are the exception: GCM
/// authenticates the whole message, so those materialize once).
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
                throw new ShardDecodeException($"Inconsistent shard set for '{first.FileName}': image counts differ.");

        // Both reassembly paths bound their buffers by the declared sizes, so sanity-check first.
        if (first.TotalLength is < 0 or > ShardEncoder.MaxFileBytes || first.OriginalLength is < 0 or > ShardEncoder.MaxFileBytes)
            throw new ShardDecodeException($"'{first.FileName}': shard header declares an implausible file size.");
        // Cross-shard geometry drives divisor/array math in both parity paths. Deserialize
        // already rejects this, but a directly-constructed shard set (session API, tests) must
        // fail cleanly rather than crash.
        if (first.StripeParity > 0 && first.StripeData < 1)
            throw new ShardDecodeException($"'{first.FileName}': shard header declares invalid stripe geometry.");

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
                throw new ShardDecodeException($"'{first.FileName}' is encrypted; supply the password with -p/--password.");
            var blob = new byte[first.TotalLength];
            source.ReadExactly(blob);
            // Newer encrypted shards bind the identity header as AAD; older ones (no FlagAuthMeta)
            // decrypt with empty AAD, which GCM treats identically to no AAD.
            ReadOnlySpan<byte> aad = (first.Flags & ShardHeader.FlagAuthMeta) != 0
                ? PayloadCipher.BuildAad(first.OriginalLength, first.Sha256, first.FileName)
                : default;
            source = new MemoryStream(cipher.Decrypt(blob, password, first.FileName, aad));
        }
        if (compressed)
        {
            source = (first.Flags & ShardHeader.FlagBrotli) != 0
                ? new BrotliStream(source, CompressionMode.Decompress)
                : new DeflateStream(source, CompressionMode.Decompress);
        }

        // Archives restore into a directory; the tar itself is a transient temp file.
        string outPath = archive
            ? Path.Combine(Path.GetTempPath(), $"qrshard-{first.FileId:x16}.tar")
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
            throw new ShardDecodeException($"'{first.FileName}': the reassembled stream failed to decompress. A shard is corrupt beyond recovery.");
        }
        finally
        {
            source.Dispose();
        }

        if (written != first.OriginalLength || !sha.AsSpan().SequenceEqual(first.Sha256))
        {
            TryDelete(outPath);
            throw new ShardDecodeException($"'{first.FileName}': SHA-256 of the reassembled file does not match the original. A shard was corrupted.");
        }

        if (archive)
        {
            string destDir = outputPath ?? Path.Combine(Environment.CurrentDirectory, Path.GetFileNameWithoutExtension(first.FileName));
            try
            {
                ExtractTar(outPath, destDir);
            }
            finally
            {
                TryDelete(outPath);
            }
            log($"  SHA-256 verified ✓  '{first.FileName}' → extracted to {destDir}");
            return new RestoredFile(first.FileName, destDir, written);
        }

        log($"  SHA-256 verified ✓  '{first.FileName}' → {outPath} ({written:N0} bytes)");
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
        using var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            string target = Path.GetFullPath(Path.Combine(destRoot, entry.Name));
            if (!target.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != destRoot)
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

    private static string ResolveOutputPath(ShardHeader first, string? outputPath)
    {
        string outPath = outputPath ?? Path.Combine(Environment.CurrentDirectory, first.FileName);
        if (outputPath is null && File.Exists(outPath))
            outPath = Path.Combine(Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(first.FileName)}.restored{Path.GetExtension(first.FileName)}");
        return outPath;
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
                $"'{first.FileName}': missing image(s) {string.Join(", ", missing.Select(i => i + 1))} of {first.Count}. " +
                "Capture them and decode again.");

        if (byIndex.Sum(s => (long)s!.Payload.Length) != first.TotalLength)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

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
