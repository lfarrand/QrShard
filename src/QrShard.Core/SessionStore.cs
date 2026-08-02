using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>Persists decoded shards between decode runs.</summary>
internal interface ISessionStore
{
    /// <summary>
    /// Opens one exclusive load/update/delete transaction. The lease is process-wide and
    /// cross-process: callers must keep it for the complete load/merge/save-or-delete operation.
    /// </summary>
    ISessionTransaction Open(string path);

    // Small compatibility helpers for the verify path and focused format tests. Mutating CLI
    // paths use Open directly so no load/save race can exist between these two calls.
    List<DecodedShard> Load(string path);

    void Save(string path, IReadOnlyCollection<DecodedShard> shards);
}

internal interface ISessionTransaction : IDisposable
{
    IReadOnlyList<DecodedShard> Shards { get; }

    /// <summary>A visible warning when a validated prefix was recovered from a torn final append.</summary>
    string? RecoveryNotice { get; }

    /// <summary>Ordinals durably erased because two CRC-valid candidates disagreed.</summary>
    int ConflictedShardCount { get; }

    /// <summary>Whether this candidate belongs to an already-terminal conflicted ordinal.</summary>
    bool IsConflicted(DecodedShard shard);

    /// <summary>Appends only shards not already durably represented by this session.</summary>
    void Save(IReadOnlyCollection<DecodedShard> shards);

    /// <summary>Deletes the validated session owned by this transaction.</summary>
    void Delete();
}

/// <summary>
/// Versioned, append-only session journal. Version 2 starts with a CRC-protected format header,
/// then stores each shard in an independently CRC-framed record. A crash can therefore leave at
/// most one torn tail: every earlier frame is still validated and reusable. Corruption anywhere
/// except that final incomplete frame is a hard error; an unrelated or future-version file is
/// never treated as an empty session and can consequently never be overwritten or deleted.
///
/// The sidecar lease is deliberately persistent. Deleting a lock file after closing it opens an
/// inode/name race on Unix (a new owner can acquire the old inode while another creates the name),
/// whereas retaining one owner-only zero-byte file gives every process one stable lock object.
/// </summary>
internal sealed class SessionStore(Crc crc, AppSettings settings) : ISessionStore
{
    private readonly record struct ShardKey(ulong FileId, int Index, bool IsParity);

    private static readonly byte[] Magic = "QRSS"u8.ToArray();
    private const byte LegacyVersion = 1;
    private const byte JournalVersion = 2;
    private const int FormatHeaderBytes = 9; // magic + version + CRC-32 of those five bytes
    private const int MaxHeaderBytes = 8 * 1024; // wire headers are at most ~4.2 KiB
    internal const int MaxEntries = 1_000_000;
    private const int MaxJournalFrames = MaxEntries * 2;
    private const int MaxEntryBytes = 512 * 1024 * 1024;

    // Session payload, headers, provenance and collection entries are live decoder memory, so
    // conservatively charge them to the same explicit operator budget. This is an admission
    // ceiling, not a promise that every byte of the remaining process fits.
    private readonly long maxStoredBytes = checked((long)settings.DecodeMemoryBudgetMB * 1_000_000);

    // Match the decoder's independent per-input metadata allowance as well as its byte ceiling.
    // Without this, millions of tiny payloads can fit the byte sum while their object/dictionary
    // state cannot be seeded into the same configured decode budget on the next run.
    private int MaxStoredEntries => Math.Min(MaxEntries,
        ShardDecoder.SuccessfulShardRetentionBudget.MaximumInputCountForByteLimit(maxStoredBytes));

    // Retained decoded state is capped at maxStoredBytes. A terminal conflict may add one
    // second valid record per key, plus framing, so three times that budget is a conservative hard
    // ceiling for the physical journal as well. This prevents conflict records becoming disk DoS.
    private long MaxJournalFileBytes => maxStoredBytes > (long.MaxValue - FormatHeaderBytes) / 3
        ? long.MaxValue
        : FormatHeaderBytes + 3 * maxStoredBytes;

    public SessionStore() : this(new Crc(), new AppSettings())
    {
    }

    /// <summary>Focused-test constructor for exercising admission limits without large fixtures.</summary>
    internal SessionStore(long maxStoredBytes) : this(new Crc(), new AppSettings())
    {
        if (maxStoredBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxStoredBytes));
        this.maxStoredBytes = maxStoredBytes;
    }

    public ISessionTransaction Open(string path) => new Transaction(this, path);

    public List<DecodedShard> Load(string path)
    {
        using var transaction = Open(path);
        return [.. transaction.Shards];
    }

    public void Save(string path, IReadOnlyCollection<DecodedShard> shards)
    {
        using var transaction = Open(path);
        transaction.Save(shards);
    }

    private sealed class Transaction : ISessionTransaction
    {
        private readonly SessionStore store;
        private readonly string path;
        private readonly FileStream lease;
        private readonly List<DecodedShard> shards;
        private readonly Dictionary<ShardKey, int> shardIndexes;
        // A null value is a durable terminal erasure: two different, individually valid
        // candidates were observed for this ordinal.  Keeping the tombstone prevents a later
        // arrival (or a restart) from making whichever candidate happened to arrive last win.
        private readonly Dictionary<ShardKey, DecodedShard?> byKey;
        private readonly Dictionary<ulong, ShardHeader> families;
        private FileStream? append;
        private long accountedBytes;
        private long validLength;
        private long observedLength;
        private int journalFrames;
        private bool exists;
        private bool tornTail;
        private bool deleted;
        private bool disposed;
        private byte[] expectedIdentity;

        internal Transaction(SessionStore store, string requestedPath)
        {
            this.store = store;
            path = Path.GetFullPath(requestedPath);
            string? directory = Path.GetDirectoryName(path);
            if (directory is null)
                throw new ArgumentException($"Session path '{ShardHeader.Display(requestedPath)}' has no parent directory.");
            Directory.CreateDirectory(directory);

            if (Directory.Exists(path))
                throw new IOException($"Session path '{ShardHeader.Display(path)}' is a directory, not a session file.");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Session path '{ShardHeader.Display(path)}' is a symbolic link or reparse point; use the target path explicitly.");

            string lockPath = path + ".lock";
            RejectReparsePoint(lockPath, "Session lease");
            if (File.Exists(lockPath) && new FileInfo(lockPath).Length != 0)
                throw new IOException(
                    $"Session lease path '{ShardHeader.Display(lockPath)}' is not an empty QrShard lease file; refusing to modify it.");
            lease = AcquireLease(lockPath);
            try
            {
                var loaded = store.LoadExisting(path);
                shards = loaded.Shards;
                accountedBytes = loaded.AccountedBytes;
                validLength = loaded.ValidLength;
                observedLength = loaded.PhysicalLength;
                journalFrames = loaded.JournalFrames;
                exists = loaded.Exists;
                tornTail = loaded.TornTail;
                expectedIdentity = loaded.IdentityHash;
                byKey = shards.ToDictionary(KeyOf, static shard => (DecodedShard?)shard);
                shardIndexes = new Dictionary<ShardKey, int>(shards.Count);
                for (int i = 0; i < shards.Count; i++)
                    shardIndexes.Add(KeyOf(shards[i]), i);
                foreach (ShardKey conflict in loaded.ConflictedKeys)
                    byKey.Add(conflict, null);
                families = loaded.Families;
                var notices = new List<string>(2);
                if (tornTail)
                    notices.Add($"Session '{ShardHeader.Display(path)}' ended with an incomplete final journal record; " +
                        $"recovered {shards.Count:N0} fully validated shard(s). The torn tail will be repaired on the next save.");
                if (loaded.ConflictedKeys.Count > 0)
                    notices.Add($"Session contains {loaded.ConflictedKeys.Count:N0} terminal conflicted ordinal(s); " +
                        "each is treated as missing, and later copies cannot select a winner.");
                RecoveryNotice = notices.Count == 0 ? null : string.Join(' ', notices);

                if (loaded.IsLegacy)
                {
                    // A complete v1 file is migrated only after every entry has validated. The
                    // private, flushed v2 snapshot is atomically published over it, so a failed
                    // conversion leaves the original bytes untouched.
                    expectedIdentity = store.WriteSnapshotAtomic(path, shards, replaceExisting: true);
                    validLength = new FileInfo(path).Length;
                    observedLength = validLength;
                    journalFrames = shards.Count;
                    exists = true;
                    TightenOwnerOnly(path);
                }
                else if (exists)
                {
                    TightenOwnerOnly(path);
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public IReadOnlyList<DecodedShard> Shards => shards;

        public string? RecoveryNotice { get; }

        public int ConflictedShardCount => byKey.Count - shards.Count;

        public bool IsConflicted(DecodedShard shard)
        {
            if (families.TryGetValue(shard.Header.FileId, out ShardHeader? family) &&
                !family.HasSameFamilyAs(shard.Header))
                throw new InvalidDataException(
                    $"Session candidate contains inconsistent metadata for file {shard.Header.FileId:x16}.");
            return byKey.TryGetValue(KeyOf(shard), out DecodedShard? prior) && prior is null;
        }

        public void Save(IReadOnlyCollection<DecodedShard> incoming)
        {
            ThrowIfUnavailable();
            var additions = new List<(DecodedShard Shard, byte[] Header, bool Conflict)>();
            var staged = new Dictionary<ShardKey, DecodedShard?>(byKey);
            var stagedFamilies = new Dictionary<ulong, ShardHeader>(families);
            long projectedBytes = accountedBytes;
            foreach (DecodedShard shard in incoming)
            {
                var key = KeyOf(shard);
                if (shard.IsTerminalConflict)
                {
                    if (shard.Payload.Length != 0)
                        throw new InvalidDataException("A terminal-conflict marker must not contain payload bytes.");
                    AddOrValidateFamily(stagedFamilies, shard.Header, "Session update");
                    if (staged.TryGetValue(key, out DecodedShard? markedPrior))
                    {
                        if (markedPrior is null)
                            continue;
                        DecodedShard witness = CreateConflictWitness(markedPrior.Header, markedPrior.Payload);
                        additions.Add((witness, ValidateShard(witness), Conflict: true));
                        staged[key] = null;
                        projectedBytes = checked(projectedBytes - RetainedBytes(markedPrior) +
                            ConflictRetainedBytes(markedPrior.Header, path));
                    }
                    else
                    {
                        if (staged.Count >= store.MaxStoredEntries)
                            throw new InvalidDataException(
                                $"Session would exceed its {store.MaxStoredEntries:N0}-entry retained-state limit.");
                        // A shared decode batch has already validated and compared both candidates,
                        // but intentionally released their potentially huge payloads. Two tiny,
                        // ordinary valid records encode the same terminal tombstone durably.
                        DecodedShard firstWitness = CreateConflictWitness(shard.Header, [1]);
                        DecodedShard secondWitness = CreateConflictWitness(shard.Header, firstWitness.Payload);
                        long conflictCharge = ConflictRetainedBytes(shard.Header, path);
                        // Loading the journal sees the first one-byte witness before the second
                        // frame turns it into a tombstone, so that bounded transient must fit too.
                        if (projectedBytes + conflictCharge + firstWitness.Payload.Length > store.maxStoredBytes)
                            throw new InvalidDataException(
                                $"Session would exceed its {store.maxStoredBytes / 1_000_000:N0} MB decoded-data budget.");
                        additions.Add((firstWitness, ValidateShard(firstWitness), Conflict: false));
                        additions.Add((secondWitness, ValidateShard(secondWitness), Conflict: true));
                        staged.Add(key, null);
                        projectedBytes = checked(projectedBytes + conflictCharge);
                    }
                    continue;
                }
                if (staged.TryGetValue(key, out DecodedShard? prior) && prior is not null &&
                    ReferenceEquals(prior.Header, shard.Header) &&
                    ReferenceEquals(prior.Payload, shard.Payload))
                    continue;

                byte[] serialized = ValidateShard(shard);
                AddOrValidateFamily(stagedFamilies, shard.Header, "Session update");
                if (staged.TryGetValue(key, out prior))
                {
                    // Once conflicted, the ordinal stays erased.  Do not let a third candidate
                    // select a winner, and do not grow the journal with repeats forever.
                    if (prior is null || Equivalent(prior, shard))
                        continue;

                    DecodedShard witness = CreateConflictWitness(prior.Header, prior.Payload);
                    additions.Add((witness, ValidateShard(witness), Conflict: true));
                    staged[key] = null;
                    projectedBytes = checked(projectedBytes - RetainedBytes(prior) +
                        ConflictRetainedBytes(prior.Header, path));
                    continue;
                }

                if (staged.Count >= store.MaxStoredEntries)
                    throw new InvalidDataException(
                        $"Session would exceed its {store.MaxStoredEntries:N0}-entry retained-state limit.");
                DecodedShard persisted = PersistedShard(shard);
                long charge = RetainedBytes(persisted);
                if (projectedBytes + charge > store.maxStoredBytes)
                    throw new InvalidDataException(
                        $"Session would exceed its {store.maxStoredBytes / 1_000_000:N0} MB decoded-data budget.");
                additions.Add((persisted, serialized, Conflict: false));
                staged.Add(key, persisted);
                projectedBytes += charge;
            }

            long projectedJournalLength = exists ? validLength : FormatHeaderBytes;
            foreach (var addition in additions)
            {
                projectedJournalLength = checked(projectedJournalLength +
                    addition.Header.Length + addition.Shard.Payload.Length + 16L);
                if (projectedJournalLength > store.MaxJournalFileBytes)
                    throw new InvalidDataException(
                        $"Session journal would exceed its {store.MaxJournalFileBytes / 1_000_000:N0} MB physical-size budget.");
            }
            if ((long)journalFrames + additions.Count > MaxJournalFrames)
                throw new InvalidDataException(
                    $"Session would exceed the explicit limit of {MaxJournalFrames:N0} journal frames.");

            if (!exists)
            {
                // The first publication is atomic, so a killed process cannot leave a file with a
                // half-written format header that future versions must mistake for corruption.
                var initial = new List<DecodedShard>(shards.Count + additions.Count);
                initial.AddRange(shards);
                initial.AddRange(additions.Select(a => a.Shard));
                expectedIdentity = store.WriteSnapshotAtomic(path, initial, replaceExisting: false);
                exists = true;
                tornTail = false;
                validLength = new FileInfo(path).Length;
                observedLength = validLength;
                journalFrames = additions.Count;
                foreach (var addition in additions)
                    ApplyAddition(addition.Shard, addition.Conflict);
                // Keep the exact published object open for subsequent appends. On Windows this
                // also prevents pathname replacement; on Unix the handle continues to identify
                // the original inode even if a non-cooperating process swaps the directory entry.
                EnsureAppendStream();
                return;
            }

            if (additions.Count == 0 && !tornTail)
                return;

            EnsureAppendStream();
            foreach (var addition in additions)
                WriteFrame(append!, addition.Shard, addition.Header);
            append!.Flush(flushToDisk: true);
            validLength = append.Position;
            observedLength = validLength;
            journalFrames = checked(journalFrames + additions.Count);
            tornTail = false;
            foreach (var addition in additions)
                ApplyAddition(addition.Shard, addition.Conflict);
        }

        public void Delete()
        {
            ThrowIfUnavailable();
            byte[] identity = append is null ? expectedIdentity : HashOpenStream(append);
            CloseAppend(durable: false);
            if (!exists)
            {
                deleted = true;
                return;
            }

            // Move the directory entry to an unpredictable sibling first, then authenticate the
            // moved bytes before deleting them. This closes the validate-then-delete pathname race:
            // a same-size replacement with a copied QRSS header is restored (or preserved in the
            // quarantine if another object appeared at the original name), never destroyed.
            string quarantine = Path.Combine(Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.qrshard-delete-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Move(path, quarantine, overwrite: false);
                long actualLength = new FileInfo(quarantine).Length;
                byte[] actualIdentity;
                using (var stream = new FileStream(quarantine, FileMode.Open, FileAccess.Read, FileShare.Read))
                    actualIdentity = SHA256.HashData(stream);
                if (actualLength != observedLength ||
                    !CryptographicOperations.FixedTimeEquals(identity, actualIdentity))
                {
                    RestoreUnexpectedReplacement(quarantine);
                    throw new InvalidDataException(
                        "Session changed before it could be deleted; the replacement was preserved and deletion was refused.");
                }
                File.Delete(quarantine);
            }
            catch
            {
                // A failure after the move must remain recoverable. Restore only into an empty
                // pathname; never overwrite a newer object installed concurrently.
                if (File.Exists(quarantine))
                    RestoreUnexpectedReplacement(quarantine);
                throw;
            }
            exists = false;
            deleted = true;
        }

        private void RestoreUnexpectedReplacement(string quarantine)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                File.Move(quarantine, path, overwrite: false);
                return;
            }
            throw new InvalidDataException(
                $"Session pathname changed during deletion; preserved the displaced file at " +
                $"'{ShardHeader.Display(quarantine)}' rather than overwriting either file.");
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                CloseAppend(durable: !deleted);
            }
            finally
            {
                lease.Dispose();
            }
        }

        private void EnsureAppendStream()
        {
            if (append is not null)
                return;
            append = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            store.ValidateFormatHeader(append);
            if (append.Length != observedLength)
            {
                CloseAppend(durable: false);
                throw new InvalidDataException("Session changed while its transaction was open.");
            }
            byte[] actualIdentity = HashOpenStream(append);
            if (!CryptographicOperations.FixedTimeEquals(expectedIdentity, actualIdentity))
            {
                CloseAppend(durable: false);
                throw new InvalidDataException("Session changed while its transaction was open; refusing to modify it.");
            }
            if (tornTail)
            {
                append.SetLength(validLength);
                append.Flush(flushToDisk: false);
            }
            append.Position = validLength;
        }

        private static byte[] HashOpenStream(FileStream stream)
        {
            stream.Flush(flushToDisk: false);
            long position = stream.Position;
            stream.Position = 0;
            byte[] hash = SHA256.HashData(stream);
            stream.Position = position;
            return hash;
        }

        private void AddKnown(DecodedShard shard)
        {
            shard = PersistedShard(shard);
            shardIndexes.Add(KeyOf(shard), shards.Count);
            shards.Add(shard);
            byKey.Add(KeyOf(shard), shard);
            families.TryAdd(shard.Header.FileId, shard.Header);
            accountedBytes = checked(accountedBytes + RetainedBytes(shard));
        }

        private DecodedShard PersistedShard(DecodedShard shard) =>
            shard.SourceFile == path ? shard : shard with { SourceFile = path };

        private void ApplyAddition(DecodedShard shard, bool conflict)
        {
            if (!conflict)
            {
                AddKnown(shard);
                return;
            }

            ShardKey key = KeyOf(shard);
            if (!byKey.TryGetValue(key, out DecodedShard? prior) || prior is null)
                throw new InvalidDataException("Session conflict journal state is inconsistent.");
            if (!shardIndexes.Remove(key, out int index) ||
                !ReferenceEquals(shards[index], prior))
                throw new InvalidDataException("Session conflict journal state is inconsistent.");
            int lastIndex = shards.Count - 1;
            if (index != lastIndex)
            {
                DecodedShard moved = shards[lastIndex];
                shards[index] = moved;
                shardIndexes[KeyOf(moved)] = index;
            }
            shards.RemoveAt(lastIndex);
            accountedBytes = checked(accountedBytes - RetainedBytes(prior) +
                ConflictRetainedBytes(shard.Header, path));
            byKey[key] = null;
        }

        private void CloseAppend(bool durable)
        {
            FileStream? stream = append;
            append = null;
            if (stream is null)
                return;
            try
            {
                if (durable)
                    stream.Flush(flushToDisk: true);
            }
            finally
            {
                stream.Dispose();
            }
        }

        private void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (deleted)
                throw new InvalidOperationException("The session was already deleted by this transaction.");
        }
    }

    private sealed record LoadResult(List<DecodedShard> Shards, long AccountedBytes,
        long ValidLength, long PhysicalLength, bool Exists, bool IsLegacy, bool TornTail,
        HashSet<ShardKey>? Conflicts = null, Dictionary<ulong, ShardHeader>? FamilyMap = null,
        int JournalFrames = 0, byte[]? ContentIdentity = null)
    {
        internal byte[] IdentityHash => ContentIdentity ?? SHA256.HashData(ReadOnlySpan<byte>.Empty);
        internal HashSet<ShardKey> ConflictedKeys => Conflicts ?? [];
        internal Dictionary<ulong, ShardHeader> Families => FamilyMap ?? [];
    }

    private LoadResult LoadExisting(string path)
    {
        if (!File.Exists(path))
            return new([], 0, 0, 0, Exists: false, IsLegacy: false, TornTail: false);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > MaxJournalFileBytes)
            throw new InvalidDataException(
                $"Session exceeds its {MaxJournalFileBytes / 1_000_000:N0} MB physical-size budget.");
        if (fs.Length < 5)
            throw new InvalidDataException($"'{ShardHeader.Display(path)}' is not a QrShard session file (header is incomplete).");
        Span<byte> prefix = stackalloc byte[5];
        fs.ReadExactly(prefix);
        if (!prefix[..4].SequenceEqual(Magic))
            throw new InvalidDataException($"'{ShardHeader.Display(path)}' is not a QrShard session file; refusing to modify it.");

        LoadResult loaded = prefix[4] switch
        {
            LegacyVersion => LoadLegacy(fs, path),
            JournalVersion => LoadJournal(fs, path, prefix),
            _ => throw new InvalidDataException(
                $"Session '{ShardHeader.Display(path)}' uses unsupported format version {prefix[4]}; refusing to modify it."),
        };
        fs.Position = 0;
        return loaded with { ContentIdentity = SHA256.HashData(fs) };
    }

    private LoadResult LoadLegacy(FileStream fs, string path)
    {
        // v1: magic/version already consumed, then count and unframed header/payload pairs.
        Span<byte> intBytes = stackalloc byte[4];
        ReadExactlyOrInvalid(fs, intBytes, "legacy session count");
        int count = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
        if (count is < 0 or > MaxEntries)
            throw new InvalidDataException(
                $"Legacy session declares {count:N0} entries; supported range is 0-{MaxEntries:N0}.");

        var result = new List<DecodedShard>(Math.Min(count, 16_384));
        var byKey = new Dictionary<ShardKey, DecodedShard>();
        var families = new Dictionary<ulong, ShardHeader>();
        long accounted = 0;
        for (int i = 0; i < count; i++)
        {
            ReadExactlyOrInvalid(fs, intBytes, $"legacy entry {i + 1} header length");
            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
            if (headerLength is < 92 or > MaxHeaderBytes || headerLength > fs.Length - fs.Position - sizeof(int))
                throw new InvalidDataException($"Legacy session entry {i + 1} has an invalid header length.");
            byte[] headerBytes = new byte[headerLength];
            ReadExactlyOrInvalid(fs, headerBytes, $"legacy entry {i + 1} header");
            ShardHeader? header = ShardHeader.Deserialize(headerBytes, out int parsedHeaderLength);
            if (header is null || parsedHeaderLength != headerLength)
                throw new InvalidDataException($"Legacy session entry {i + 1} has a corrupt or unsupported shard header.");

            ReadExactlyOrInvalid(fs, intBytes, $"legacy entry {i + 1} payload length");
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
            if (payloadLength < 0 || payloadLength != header.PayloadLength ||
                payloadLength > MaxEntryBytes || payloadLength > fs.Length - fs.Position)
                throw new InvalidDataException($"Legacy session entry {i + 1} has an invalid payload length.");

            var key = new ShardKey(header.FileId, header.Index, header.IsParity);
            if (byKey.TryGetValue(key, out DecodedShard? prior))
            {
                if (!headerBytes.AsSpan().SequenceEqual(prior.Header.Serialize()) ||
                    !ReadAndComparePayload(fs, prior.Payload))
                    throw new InvalidDataException($"Legacy session entry {i + 1} conflicts with an earlier shard.");
                continue;
            }

            if (byKey.Count >= MaxStoredEntries)
                throw new InvalidDataException(
                    $"Session exceeds its {MaxStoredEntries:N0}-entry retained-state limit.");
            long charge = RetainedBytes(header, payloadLength, path);
            if (accounted + charge > maxStoredBytes)
                throw new InvalidDataException(
                    $"Session exceeds its {maxStoredBytes / 1_000_000:N0} MB decoded-data budget.");
            byte[] payload = new byte[payloadLength];
            ReadExactlyOrInvalid(fs, payload, $"legacy entry {i + 1} payload");
            if (crc.Crc32(payload) != header.PayloadCrc32)
                throw new InvalidDataException($"Legacy session entry {i + 1} payload CRC is corrupt.");
            AddOrValidateFamily(families, header, $"Legacy session entry {i + 1}");
            var shard = new DecodedShard(header, payload, path, 0, 0);
            byKey.Add(key, shard);
            result.Add(shard);
            accounted += charge;
        }
        if (fs.Position != fs.Length)
            throw new InvalidDataException("Legacy session has unexpected trailing data.");
        return new(result, accounted, fs.Length, fs.Length, Exists: true, IsLegacy: true, TornTail: false,
            FamilyMap: families, JournalFrames: result.Count);
    }

    private LoadResult LoadJournal(FileStream fs, string path, ReadOnlySpan<byte> prefix)
    {
        Span<byte> crcBytes = stackalloc byte[4];
        ReadExactlyOrInvalid(fs, crcBytes, "session format-header CRC");
        if (BinaryPrimitives.ReadUInt32LittleEndian(crcBytes) != crc.Crc32(prefix))
            throw new InvalidDataException("Session format header is corrupt.");

        var byKey = new Dictionary<ShardKey, DecodedShard?>();
        var conflicts = new HashSet<ShardKey>();
        var families = new Dictionary<ulong, ShardHeader>();
        long accounted = 0;
        long validLength = FormatHeaderBytes;
        bool torn = false;
        Span<byte> intBytes = stackalloc byte[4];

        int frameCount = 0;
        while (fs.Position < fs.Length)
        {
            long frameStart = fs.Position;
            long remaining = fs.Length - frameStart;
            if (remaining < sizeof(int))
            {
                torn = true;
                break;
            }
            fs.ReadExactly(intBytes);
            int frameLength = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
            if (frameLength is < 8 or > MaxEntryBytes + MaxHeaderBytes + 8)
                throw new InvalidDataException($"Session journal frame at offset {frameStart} declares an invalid length.");
            if ((long)frameLength + sizeof(uint) > fs.Length - fs.Position)
            {
                torn = true;
                break;
            }
            if (++frameCount > MaxJournalFrames)
                throw new InvalidDataException(
                    $"Session exceeds the explicit limit of {MaxJournalFrames:N0} journal frames.");

            var frameCrc = new System.IO.Hashing.Crc32();
            fs.ReadExactly(intBytes);
            frameCrc.Append(intBytes);
            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
            if (headerLength is < 92 or > MaxHeaderBytes || frameLength < headerLength + 8)
                throw new InvalidDataException($"Session journal frame at offset {frameStart} has an invalid header length.");
            byte[] headerBytes = new byte[headerLength];
            fs.ReadExactly(headerBytes);
            frameCrc.Append(headerBytes);
            ShardHeader? header = ShardHeader.Deserialize(headerBytes, out int parsedHeaderLength);
            if (header is null || parsedHeaderLength != headerLength)
                throw new InvalidDataException($"Session journal frame at offset {frameStart} has a corrupt shard header.");

            fs.ReadExactly(intBytes);
            frameCrc.Append(intBytes);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(intBytes);
            if (payloadLength < 0 || payloadLength != header.PayloadLength ||
                payloadLength > MaxEntryBytes || frameLength != headerLength + payloadLength + 8)
                throw new InvalidDataException($"Session journal frame at offset {frameStart} has an invalid payload length.");

            var key = new ShardKey(header.FileId, header.Index, header.IsParity);
            byte[]? payload = null;
            bool duplicate = byKey.TryGetValue(key, out DecodedShard? prior);
            bool duplicateMatches = prior is not null &&
                headerBytes.AsSpan().SequenceEqual(prior.Header.Serialize()) &&
                prior.Payload.Length == payloadLength;
            bool payloadMatches = false;
            uint payloadCrc;
            if (duplicate)
            {
                if (prior is not null && prior.Payload.Length == payloadLength)
                    ReadPayloadForComparison(fs, payloadLength, frameCrc, prior.Payload,
                        out payloadCrc, out payloadMatches);
                else
                    ReadPayloadDiscard(fs, payloadLength, frameCrc, out payloadCrc);

                if (prior is not null && prior.Payload.Length == payloadLength)
                    duplicateMatches &= payloadMatches;
            }
            else
            {
                if (byKey.Count >= MaxStoredEntries)
                    throw new InvalidDataException(
                        $"Session exceeds its {MaxStoredEntries:N0}-entry retained-state limit.");
                long charge = RetainedBytes(header, payloadLength, path);
                if (accounted + charge > maxStoredBytes)
                    throw new InvalidDataException(
                        $"Session exceeds its {maxStoredBytes / 1_000_000:N0} MB decoded-data budget.");
                payload = new byte[payloadLength];
                ReadPayloadInto(fs, payload, frameCrc, out payloadCrc);
            }

            fs.ReadExactly(crcBytes);
            uint expectedFrameCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
            if (expectedFrameCrc != frameCrc.GetCurrentHashAsUInt32())
                throw new InvalidDataException($"Session journal is corrupt at offset {frameStart}; refusing a partial load.");

            if (payloadCrc != header.PayloadCrc32)
                throw new InvalidDataException($"Session journal frame at offset {frameStart} has a corrupt payload CRC.");
            if (!duplicate)
            {
                AddOrValidateFamily(families, header,
                    $"Session journal frame at offset {frameStart}");
                var shard = new DecodedShard(header, payload!, path, 0, 0);
                byKey.Add(key, shard);
                accounted += RetainedBytes(header, payloadLength, path);
            }
            else
            {
                AddOrValidateFamily(families, header,
                    $"Session journal frame at offset {frameStart}");
                if (prior is not null && !duplicateMatches)
                {
                    accounted = checked(accounted - RetainedBytes(prior) +
                        ConflictRetainedBytes(header, path));
                    byKey[key] = null;
                    conflicts.Add(key);
                }
                // prior == null is an already-terminal erasure; later frames cannot choose a
                // winner and are ignored after their framing, CRC and family have validated.
            }
            validLength = fs.Position;
        }

        var result = byKey.Values.Where(static shard => shard is not null)
            .Select(static shard => shard!).ToList();
        return new(result, accounted, validLength, fs.Length, Exists: true, IsLegacy: false, TornTail: torn,
            Conflicts: conflicts, FamilyMap: families, JournalFrames: frameCount);
    }

    private static void ReadPayloadInto(FileStream fs, byte[] destination,
        System.IO.Hashing.Crc32 frameCrc, out uint payloadCrc)
    {
        var payloadHasher = new System.IO.Hashing.Crc32();
        int offset = 0;
        while (offset < destination.Length)
        {
            int take = Math.Min(16 * 1024, destination.Length - offset);
            Span<byte> target = destination.AsSpan(offset, take);
            fs.ReadExactly(target);
            frameCrc.Append(target);
            payloadHasher.Append(target);
            offset += take;
        }
        payloadCrc = payloadHasher.GetCurrentHashAsUInt32();
    }

    private static void ReadPayloadForComparison(FileStream fs, int length,
        System.IO.Hashing.Crc32 frameCrc, byte[] expected, out uint payloadCrc, out bool equal)
    {
        var payloadHasher = new System.IO.Hashing.Crc32();
        Span<byte> scratch = stackalloc byte[16 * 1024];
        int offset = 0;
        equal = expected.Length == length;
        while (offset < length)
        {
            int take = Math.Min(scratch.Length, length - offset);
            fs.ReadExactly(scratch[..take]);
            frameCrc.Append(scratch[..take]);
            payloadHasher.Append(scratch[..take]);
            equal &= scratch[..take].SequenceEqual(expected.AsSpan(offset, take));
            offset += take;
        }
        payloadCrc = payloadHasher.GetCurrentHashAsUInt32();
    }

    private static void ReadPayloadDiscard(FileStream fs, int length,
        System.IO.Hashing.Crc32 frameCrc, out uint payloadCrc)
    {
        var payloadHasher = new System.IO.Hashing.Crc32();
        Span<byte> scratch = stackalloc byte[16 * 1024];
        int offset = 0;
        while (offset < length)
        {
            int take = Math.Min(scratch.Length, length - offset);
            fs.ReadExactly(scratch[..take]);
            frameCrc.Append(scratch[..take]);
            payloadHasher.Append(scratch[..take]);
            offset += take;
        }
        payloadCrc = payloadHasher.GetCurrentHashAsUInt32();
    }

    private static bool ReadAndComparePayload(FileStream fs, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[16 * 1024];
        int offset = 0;
        bool equal = true;
        while (offset < expected.Length)
        {
            int take = Math.Min(buffer.Length, expected.Length - offset);
            ReadExactlyOrInvalid(fs, buffer[..take], "duplicate legacy payload");
            equal &= buffer[..take].SequenceEqual(expected.AsSpan(offset, take));
            offset += take;
        }
        return equal;
    }

    private byte[] WriteSnapshotAtomic(string path, IReadOnlyCollection<DecodedShard> shards,
        bool replaceExisting)
    {
        if (shards.Count > MaxJournalFrames)
            throw new InvalidDataException($"Session exceeds the explicit limit of {MaxJournalFrames:N0} journal frames.");
        string directory = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.qrshard-{Guid.NewGuid():N}.tmp");
        byte[] identity;
        try
        {
            using (var fs = ShardAssembler.CreatePrivateStagingFile(temp))
            {
                WriteFormatHeader(fs);
                foreach (DecodedShard shard in shards)
                    WriteFrame(fs, shard);
                fs.Flush(flushToDisk: true);
            }
            using (var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                identity = SHA256.HashData(read);
            File.Move(temp, path, overwrite: replaceExisting);
            return identity;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteFormatHeader(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[5];
        Magic.CopyTo(prefix);
        prefix[4] = JournalVersion;
        stream.Write(prefix);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, new Crc().Crc32(prefix));
        stream.Write(bytes);
    }

    private static void WriteFrame(Stream stream, DecodedShard shard)
    {
        byte[] header = ValidateShard(shard);
        WriteFrame(stream, shard, header);
    }

    private static void WriteFrame(Stream stream, DecodedShard shard, byte[] header)
    {
        int frameLength = checked(header.Length + shard.Payload.Length + 8);
        if (frameLength > MaxEntryBytes + MaxHeaderBytes + 8)
            throw new InvalidDataException("Shard is too large for a session journal entry.");

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, frameLength);
        stream.Write(bytes);

        var frameCrc = new System.IO.Hashing.Crc32();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, header.Length);
        stream.Write(bytes);
        frameCrc.Append(bytes);
        stream.Write(header);
        frameCrc.Append(header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, shard.Payload.Length);
        stream.Write(bytes);
        frameCrc.Append(bytes);
        stream.Write(shard.Payload);
        frameCrc.Append(shard.Payload);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, frameCrc.GetCurrentHashAsUInt32());
        stream.Write(bytes);
    }

    private void ValidateFormatHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateFormatHeader(fs);
    }

    private void ValidateFormatHeader(FileStream fs)
    {
        if (fs.Length < FormatHeaderBytes)
            throw new InvalidDataException("Session changed before it could be deleted; refusing deletion.");
        long position = fs.Position;
        fs.Position = 0;
        Span<byte> prefix = stackalloc byte[5];
        Span<byte> crcBytes = stackalloc byte[4];
        fs.ReadExactly(prefix);
        fs.ReadExactly(crcBytes);
        fs.Position = position;
        if (!prefix[..4].SequenceEqual(Magic) || prefix[4] != JournalVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(crcBytes) != crc.Crc32(prefix))
            throw new InvalidDataException("Session changed before it could be deleted; refusing deletion.");
    }

    private static byte[] ValidateShard(DecodedShard shard)
    {
        byte[] header = shard.Header.Serialize();
        ShardHeader? reparsed = ShardHeader.Deserialize(header, out int parsedLength);
        if (header.Length is < 92 or > MaxHeaderBytes || reparsed is null || parsedLength != header.Length ||
            shard.Payload.Length > MaxEntryBytes ||
            shard.Header.PayloadLength != shard.Payload.Length ||
            new Crc().Crc32(shard.Payload) != shard.Header.PayloadCrc32)
            throw new InvalidDataException("Refusing to store an invalid decoded shard in a session.");
        return header;
    }

    private static ShardKey KeyOf(DecodedShard shard) =>
        new(shard.Header.FileId, shard.Header.Index, shard.Header.IsParity);

    private static void AddOrValidateFamily(Dictionary<ulong, ShardHeader> families,
        ShardHeader header, string context)
    {
        if (families.TryGetValue(header.FileId, out ShardHeader? family))
        {
            if (!family.HasSameFamilyAs(header))
                throw new InvalidDataException(
                    $"{context} contains inconsistent metadata for file {header.FileId:x16}.");
        }
        else
        {
            families.Add(header.FileId, header);
        }
    }

    private static bool Equivalent(DecodedShard first, DecodedShard second) =>
        first.Header.Serialize().AsSpan().SequenceEqual(second.Header.Serialize()) &&
        first.Payload.AsSpan().SequenceEqual(second.Payload);

    private static DecodedShard CreateConflictWitness(ShardHeader template,
        ReadOnlySpan<byte> payloadToDifferFrom)
    {
        byte value = payloadToDifferFrom.Length == 1 && payloadToDifferFrom[0] == 0 ? (byte)1 : (byte)0;
        byte[] payload = [value];
        var header = new ShardHeader
        {
            FileId = template.FileId,
            Index = template.Index,
            Count = template.Count,
            PayloadLength = payload.Length,
            PayloadCrc32 = new Crc().Crc32(payload),
            TotalLength = template.TotalLength,
            OriginalLength = template.OriginalLength,
            Flags = template.Flags,
            Sha256 = template.Sha256,
            FileName = template.FileName,
            StripeData = template.StripeData,
            StripeParity = template.StripeParity,
        };
        return new DecodedShard(header, payload, "session-conflict", 0, 0);
    }

    private static long RetainedBytes(DecodedShard shard) =>
        RetainedBytes(shard.Header, shard.Payload.Length, shard.SourceFile);

    private static long RetainedBytes(ShardHeader header, int payloadLength, string sourceFile) =>
        checked(2L * ShardHeader.Size(header.FileName) + 2L * sourceFile.Length + payloadLength +
            ShardDecoder.SuccessfulShardRetentionBudget.PerShardOverheadBytes);

    private static long ConflictRetainedBytes(ShardHeader header, string sourceFile) =>
        RetainedBytes(header, payloadLength: 0, sourceFile);

    private static FileStream AcquireLease(string lockPath)
    {
        try
        {
            FileStream stream;
            if (OperatingSystem.IsWindows())
            {
                stream = new FileInfo(lockPath).Create(
                    FileMode.OpenOrCreate,
                    FileSystemRights.FullControl,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None,
                    ShardAssembler.PrivateFileSecurity());
            }
            else
            {
                stream = new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return stream;
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Could not acquire the exclusive lease for session '{ShardHeader.Display(Path.GetFullPath(lockPath[..^5]))}'; " +
                "another process may already be using it.", ex);
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(
                    $"{label} path '{ShardHeader.Display(path)}' is a symbolic link or reparse point; refusing it.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TightenOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            new FileInfo(path).SetAccessControl(ShardAssembler.PrivateFileSecurity());
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ReadExactlyOrInvalid(Stream stream, Span<byte> buffer, string field)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Session is truncated while reading {field}.", ex);
        }
    }

    private static void ReadExactlyOrInvalid(Stream stream, byte[] buffer, string field) =>
        ReadExactlyOrInvalid(stream, buffer.AsSpan(), field);
}
