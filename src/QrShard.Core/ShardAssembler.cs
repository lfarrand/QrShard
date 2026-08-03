using System.Formats.Tar;
using System.Buffers.Binary;
using System.IO.Compression;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    internal const int MaxArchiveEntries = 100_000;
    internal const int MaxArchiveDepth = 128;
    internal const int MaxArchivePathNodes = 200_000;
    internal const UnixFileMode PortableUnixFileModeMask =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public ShardAssembler() : this(new ParityReassembler(), new PayloadCipher())
    {
    }

    /// <summary>Reassembles already-decoded shards into output file(s). Shared by folder and video decoding.</summary>
    public List<RestoredFile> Assemble(List<DecodedShard> shards, string? outputPath, Action<string> log, string? password = null)
    {
        var groups = shards.GroupBy(s => s.Header.FileId).ToList();
        if (outputPath is not null && groups.Count > 1 && !Directory.Exists(outputPath))
            throw new ShardDecodeException("The images belong to multiple different files; omit -o or decode them separately.");

        // A mixed capture is one logical decode request. Prove every family complete and
        // internally consistent before the first path is published; otherwise sort/group order
        // made a complete sibling appear on disk just before an incomplete one threw.
        if (groups.Count > 1)
        {
            foreach (var group in groups)
            {
                List<DecodedShard> family = [.. group];
                ShardHeader first = family[0].Header;
                if (family.Any(shard => !first.HasSameFamilyAs(shard.Header)))
                    throw new ShardDecodeException(
                        $"Inconsistent shard set for '{ShardHeader.Display(first.FileName)}': repeated file metadata differs.");
                if (first.TotalLength is < 0 or > ShardEncoder.MaxFileBytes ||
                    first.OriginalLength is < 0 or > ShardEncoder.MaxFileBytes)
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}': shard header declares an implausible file size.");
                if ((first.Flags & ShardHeader.FlagEncrypted) != 0 && password is null)
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}' is encrypted; supply the password with -p/--password.");
                if (!parityReassembler.IsSetComplete(family))
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}': the shard family is incomplete or inconsistent. " +
                        "Capture the missing images and decode the mixed set again.");
            }
        }

        // Structural incompleteness has now failed before publication. Attempt every admitted
        // group before reporting a later content-verification or filesystem failure, so one
        // destination's independent I/O problem does not suppress unrelated verified outputs.
        var restored = new List<RestoredFile>();
        ShardDecodeException? failure = null;
        foreach (var group in groups)
        {
            try
            {
                restored.Add(Reassemble([.. group], outputPath, log, password));
            }
            catch (ShardDecodeException ex)
            {
                failure ??= ex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A malformed archive or one destination's filesystem failure belongs to that
                // file, not to unrelated FileIds in the same capture folder/session. Preserve the
                // best-effort multi-file contract while keeping process-wide failures such as OOM
                // and cancellation fatal.
                failure ??= new ShardDecodeException(
                    $"'{ShardHeader.Display(group.First().Header.FileName)}': restore failed " +
                    $"({ShardHeader.Display(ex.Message)}).");
            }
        }
        // Reassemble writes each admitted file as it goes; any independent runtime failure is
        // reported after the other already-preflighted families have had their restore attempt.
        if (failure is not null)
            throw failure;
        return restored;
    }

    private RestoredFile Reassemble(List<DecodedShard> shards, string? outputPath, Action<string> log, string? password)
    {
        var first = shards[0].Header;
        int count = first.Count;
        foreach (var s in shards)
            if (!first.HasSameFamilyAs(s.Header))
                throw new ShardDecodeException(
                    $"Inconsistent shard set for '{ShardHeader.Display(first.FileName)}': repeated file identity or recovery metadata differs.");

        // Both reassembly paths bound their buffers by the declared sizes, so sanity-check first.
        // Deserialize enforces these protocol invariants; repeat them because embedding/tests can
        // hand an already-constructed DecodedShard to this internal assembly path.
        if (count is < 1 or > ShardHeader.MaxImages || first.StripeData < 0 || first.StripeParity < 0 ||
            ((first.StripeData == 0) != (first.StripeParity == 0)))
            throw new ShardDecodeException(
                $"'{ShardHeader.Display(first.FileName)}': shard header declares invalid recovery geometry.");
        if (first.TotalLength is < 0 or > ShardEncoder.MaxFileBytes || first.OriginalLength is < 0 or > ShardEncoder.MaxFileBytes)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares an implausible file size.");
        // Cross-shard geometry drives divisor/array math in both parity paths. Deserialize
        // already rejects this, but a directly-constructed shard set (session API, tests) must
        // fail cleanly rather than crash.
        if (first.StripeParity > 0 && first.StripeData < 1)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares invalid stripe geometry.");

        byte[][] chunks;
        long[] chunkLengths;
        DataCandidates dataCandidates = CollectDataCandidates(shards, count);
        bool allDataPresent = dataCandidates.ByIndex.Count == count;
        if (first.StripeParity > 0 && !allDataPresent)
        {
            chunks = parityReassembler.ReassembleWithParity(shards, first, log, out int cap);
            chunkLengths = new long[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
                chunkLengths[i] = Math.Min(cap, first.TotalLength - (long)i * cap);
        }
        else
        {
            chunks = CollectContiguous(first, dataCandidates);
            chunkLengths = new long[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
                chunkLengths[i] = chunks[i].Length;
        }

        bool encrypted = (first.Flags & ShardHeader.FlagEncrypted) != 0;
        bool compressed = (first.Flags & ShardHeader.FlagCompressed) != 0;
        bool archive = (first.Flags & ShardHeader.FlagArchive) != 0;

        Stream source = new ChunkConcatStream(chunks, chunkLengths);
        byte[]? decryptedPlaintext = null;
        TemporaryDirectoryLease? archiveTemp = null;
        string payloadPath = "";
        try
        {
            if (encrypted)
            {
                if (password is null)
                    throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}' is encrypted; supply the password with -p/--password.");
                var blob = new byte[first.TotalLength];
                decryptedPlaintext = blob;
                source.ReadExactly(blob);
                // Newer encrypted shards bind the identity header as AAD; older ones (no FlagAuthMeta)
                // decrypt with empty AAD, which GCM treats identically to no AAD.
                ReadOnlySpan<byte> aad = (first.Flags & ShardHeader.FlagAuthMetaV2) != 0
                    ? PayloadCipher.BuildAadV2(first.OriginalLength, first.Sha256, first.FileName,
                        first.Flags)
                    : (first.Flags & ShardHeader.FlagAuthMeta) != 0
                        ? PayloadCipher.BuildAad(first.OriginalLength, first.Sha256, first.FileName)
                        : default;
                // Decrypting in place keeps the encrypted path to two live buffers (chunks + blob)
                // rather than three; the tag is still verified over the whole message first.
                ArraySegment<byte> plain = cipher.DecryptInPlace(blob, password, first.FileName, aad);
                source.Dispose();
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
            // refused. A random private directory removes both the predictability and the shared-root
            // exposure: it requests 0700 on Unix and has a protected owner-only DACL on Windows.
            archiveTemp = archive ? CreatePrivateTemporaryDirectory("qrshard-") : null;
            string tempDir = archiveTemp?.Path ?? "";
            if (archiveTemp is not null)
                log($"  reassembly temp directory: {tempDir}");
            string finalPath = archive ? "" : ResolveOutputPath(first, outputPath);
            // Never stream unverified bytes into the final pathname. In particular, an explicit -o
            // may already contain the user's only good copy: FileMode.Create used to truncate it
            // before decompression, length, or SHA-256 verification had succeeded. A random sibling
            // keeps the final move on one filesystem and FileMode.CreateNew prevents link/race reuse.
            payloadPath = archive
                ? Path.Combine(tempDir, "payload.tar")
                : SiblingStagingPath(finalPath);

            long written = 0;
            byte[] sha;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = CreatePrivateStagingFile(payloadPath))
                {
                    var buffer = new byte[1 << 20];
                    try
                    {
                        int n;
                        while (written <= first.OriginalLength && (n = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            hash.AppendData(buffer, 0, n);
                            written += n;
                        }
                    }
                    finally
                    {
                        // The buffer holds verified-output plaintext regardless of whether the
                        // source was encrypted. Clear the entire rented-sized working area on
                        // every success/failure path instead of leaving the last chunk in Gen 2.
                        CryptographicOperations.ZeroMemory(buffer);
                    }
                }
                sha = hash.GetHashAndReset();
            }
            catch (InvalidDataException)
            {
                throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': the reassembled stream failed to decompress. A shard is corrupt beyond recovery.");
            }

            if (written != first.OriginalLength || !sha.AsSpan().SequenceEqual(first.Sha256))
                throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': SHA-256 of the reassembled file does not match the original. A shard was corrupted.");

            if (archive)
            {
                // Extract into a private sibling and publish only after every tar entry has passed
                // containment validation. A late ../ entry or I/O failure therefore cannot leave
                // a half-restored tree or overwrite files already present in an explicit -o.
                string destDir = outputPath ?? FreeDirectory(SafeDirectoryName(first.FileName));
                ExtractTarAtomically(payloadPath, destDir);
                log($"  SHA-256 verified ✓  '{ShardHeader.Display(first.FileName)}' → extracted to " +
                    ShardHeader.Display(destDir));
                return new RestoredFile(first.FileName, destDir, written);
            }

            // Preserve the documented explicit-output behaviour, but delay replacement until the
            // staged file is complete and verified. Without -o, overwrite:false also closes the
            // check/use race after ResolveOutputPath selected an unused name.
            if (outputPath is not null && File.Exists(finalPath))
                PublishVerifiedReplacement(payloadPath, finalPath);
            else
                File.Move(payloadPath, finalPath, overwrite: outputPath is not null);
            log($"  SHA-256 verified ✓  '{ShardHeader.Display(first.FileName)}' → " +
                $"{ShardHeader.Display(finalPath)} ({written:N0} bytes)");
            return new RestoredFile(first.FileName, finalPath, written);
        }
        finally
        {
            // Clear decrypted material first: even an unexpected stream-disposal failure must
            // not bypass zeroing. Nested finally blocks likewise preserve staging cleanup.
            if (decryptedPlaintext is not null)
                CryptographicOperations.ZeroMemory(decryptedPlaintext);
            try
            {
                source.Dispose();
            }
            finally
            {
                try
                {
                    if (payloadPath.Length > 0)
                        TryDelete(payloadPath);
                }
                finally
                {
                    archiveTemp?.Dispose();
                }
            }
        }
    }

    /// <summary>Returns an unpredictable, same-directory pathname for an atomic final move.</summary>
    private static string SiblingStagingPath(string destination)
    {
        string full = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(full)!;
        string name = Path.GetFileName(full);
        return Path.Combine(parent, $".{name}.qrshard-{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Extracts a verified tar into a private sibling directory, then publishes the complete tree.
    /// Existing non-empty destinations are refused even for explicit -o: merging would make a
    /// failed restore irreversible and would retain files that are not part of the archive.
    /// </summary>
    private static void ExtractTarAtomically(string tarPath, string destDir)
    {
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destDir));
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null || destination == Path.GetPathRoot(destination))
            throw new ShardDecodeException("Refusing to extract an archive directly into a filesystem root.");
        if (File.Exists(destination))
            throw new ShardDecodeException(
                $"Cannot extract archive: '{ShardHeader.Display(destDir)}' is a file.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ShardDecodeException(
                $"Cannot extract archive: destination '{ShardHeader.Display(destDir)}' is not empty.");

        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".qrshard-{Guid.NewGuid():N}.tmp");
        using TemporaryDirectoryLease stagingLease = CreatePrivateDirectoryLease(staging);
        bool destinationExisted = Directory.Exists(destination);
        DirectoryMetadata? destinationMetadata = destinationExisted
            ? CaptureDirectoryMetadata(destination)
            : null;
        try
        {
            ExtractTar(tarPath, staging);

            // Recheck immediately before publishing so a concurrent writer is never overwritten.
            if (File.Exists(destination) ||
                (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
                throw new ShardDecodeException(
                    $"Cannot extract archive: destination '{ShardHeader.Display(destDir)}' changed and is no longer empty.");

            // Keep the staging root private until the exact leased object has been published.
            // Applying an existing 0777 mode/permissive ACL before publication would expose the
            // plaintext; applying 0555/read-only would also make a valid move fail.
            if (Directory.Exists(destination))
                Directory.Delete(destination);
            try
            {
                stagingLease.MoveTo(destination);
                if (destinationMetadata is not null)
                    ApplyDirectoryMetadata(destination, destinationMetadata);
            }
            catch
            {
                // Re-create an empty directory the caller supplied if publication itself failed.
                if (destinationExisted && !Directory.Exists(destination) && !File.Exists(destination))
                {
                    CreatePrivateDirectoryExclusive(destination);
                    ApplyDirectoryMetadata(destination, destinationMetadata!);
                }
                throw;
            }
        }
        finally
        {
            // The lease owns best-effort cleanup. If publication succeeded its old path no longer
            // exists; if anything failed it removes the private incomplete tree.
        }
    }

    /// <summary>
    /// Exclusively creates a directory with restrictive permissions in the create syscall. The
    /// managed CreateDirectory overloads succeed when the path already exists, which is unsafe for
    /// staging: a raced directory could otherwise be silently adopted.
    /// </summary>
    internal static void CreatePrivateDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
            CreatePrivateWindowsDirectory(path);
        else
            CreatePrivateUnixDirectory(path);
    }

    private static TemporaryDirectoryLease CreatePrivateDirectoryLease(string path)
    {
        CreatePrivateDirectoryExclusive(path);
        if (!OperatingSystem.IsWindows())
            return new TemporaryDirectoryLease(path, null);

        SafeFileHandle handle = OpenWindowsDirectoryLease(path);
        if (!IsVerifiedPrivateWindowsDirectory(path))
        {
            handle.Dispose();
            throw new IOException(
                $"Private staging directory '{ShardHeader.Display(path)}' changed before it could be leased.");
        }
        return new TemporaryDirectoryLease(path, handle);
    }

    [SupportedOSPlatform("windows")]
    internal static void CreatePrivateWindowsDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL creation is only available on Windows.");

        DirectorySecurity security = PrivateDirectorySecurity();
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new NativeSecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<NativeSecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            if (!NativeCreateDirectory(path, ref attributes))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException($"Could not exclusively create private directory '{ShardHeader.Display(path)}'.",
                    new Win32Exception(error));
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void CreatePrivateUnixDirectory(string path)
    {
        const uint OwnerRwx = 0x1C0; // 0700; the process umask may make this stricter
        if (NativeMkdir(path, OwnerRwx) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not exclusively create private directory '{ShardHeader.Display(path)}'.",
                new Win32Exception(error));
        }
    }

    /// <summary>
    /// Creates an unpredictable plaintext-work directory. On Windows, an open directory handle
    /// deliberately omits FILE_SHARE_DELETE for the whole operation; a permissive redirected TEMP
    /// parent therefore cannot rename/delete and replace the root after its ACL is verified.
    /// </summary>
    internal static TemporaryDirectoryLease CreatePrivateTemporaryDirectory(string prefix)
    {
        if (!OperatingSystem.IsWindows())
            return new TemporaryDirectoryLease(Directory.CreateTempSubdirectory(prefix).FullName, null);

        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string candidate = Path.Combine(tempRoot, prefix + Guid.NewGuid().ToString("N"));
            try
            {
                CreatePrivateWindowsDirectory(candidate);
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue; // exclusive native create proved this name was already occupied
            }

            SafeFileHandle handle;
            try
            {
                handle = OpenWindowsDirectoryLease(candidate);
            }
            catch (IOException) when (!Directory.Exists(candidate))
            {
                continue; // deleted in the create/open window; never adopt a replacement
            }

            if (!IsVerifiedPrivateWindowsDirectory(candidate))
            {
                handle.Dispose();
                continue; // replaced before the lease opened, or otherwise not the object created
            }
            return new TemporaryDirectoryLease(candidate, handle);
        }
        throw new IOException("Could not create and lease a private QrShard temporary directory after 32 attempts.");
    }

    internal sealed class TemporaryDirectoryLease(string path, SafeFileHandle? windowsHandle) : IDisposable
    {
        private SafeFileHandle? handle = windowsHandle;
        private int published;
        public string Path { get; } = path;

        internal void MoveTo(string destination)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.Move(Path, destination);
                Volatile.Write(ref published, 1);
                return;
            }

            SafeFileHandle leased = Volatile.Read(ref handle) ??
                throw new ObjectDisposedException(nameof(TemporaryDirectoryLease));
            RenameWindowsDirectory(leased, destination);
            Volatile.Write(ref published, 1);
        }

        public void Dispose()
        {
            SafeFileHandle? leased = Interlocked.Exchange(ref handle, null);
            if (Volatile.Read(ref published) != 0)
            {
                // Path is now a free old staging name, while the Windows handle follows the
                // published destination. Never traverse that pathname: another parent writer may
                // already have created an unrelated object there.
                leased?.Dispose();
                return;
            }
            if (leased is not null)
            {
                // Delete children while the no-delete-sharing root handle still pins this exact
                // directory object. Closing first would let a permissive TEMP parent swap the
                // pathname and turn recursive cleanup into deletion of an attacker's replacement.
                TryDeleteDirectoryContents(Path);
                leased.Dispose();
                TryDeleteEmptyDirectory(Path);
                return;
            }

            if (OperatingSystem.IsWindows())
                TryCleanupVerifiedWindowsDirectory(Path);
            else
                TryDeleteDirectory(Path); // normal Unix temp is sticky; output parents are trusted
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenWindowsDirectoryLease(string path)
    {
        const uint FileFlagBackupSemantics = 0x02000000;
        const uint GenericRead = 0x80000000;
        const uint Delete = 0x00010000;
        SafeFileHandle handle = NativeCreateFile(path, GenericRead | Delete,
            FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open,
            FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Could not lease private directory '{ShardHeader.Display(path)}'.",
                new Win32Exception(error));
        }
        return handle;
    }

    /// <summary>
    /// Renames the exact directory object held by the lease. A path-based move would require
    /// closing the no-delete-sharing handle first, reopening a swap window in a writable parent.
    /// FILE_RENAME_INFO accepts an absolute UTF-16 target and requires DELETE access on the
    /// source handle; the handle remains open on the renamed object until publication completes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RenameWindowsDirectory(SafeFileHandle handle, string destination)
    {
        byte[] targetName = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int rootDirectoryOffset = IntPtr.Size; // BOOLEAN plus native pointer-alignment padding
        int fileNameLengthOffset = checked(rootDirectoryOffset + IntPtr.Size);
        int fileNameOffset = checked(fileNameLengthOffset + sizeof(uint));
        int nativeMinimumSize = IntPtr.Size == 8 ? 24 : 16;
        byte[] info = new byte[Math.Max(nativeMinimumSize, checked(fileNameOffset + targetName.Length))];
        // ReplaceIfExists and RootDirectory remain zero. The destination was rechecked and any
        // caller-supplied empty directory was removed immediately before this call.
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(fileNameLengthOffset, sizeof(uint)),
            checked((uint)targetName.Length));
        targetName.CopyTo(info, fileNameOffset);

        GCHandle pinned = GCHandle.Alloc(info, GCHandleType.Pinned);
        try
        {
            const int FileRenameInfo = 3;
            if (!NativeSetFileInformationByHandle(handle, FileRenameInfo, pinned.AddrOfPinnedObject(),
                    checked((uint)info.Length)))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Could not atomically publish archive directory to '{ShardHeader.Display(destination)}'.",
                    new Win32Exception(error));
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsVerifiedPrivateWindowsDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return false;
        DirectorySecurity acl = new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier current = CurrentWindowsUserSid();
        if (!acl.AreAccessRulesProtected || !current.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            return false;
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        return rules.Count > 0 && rules.All(rule => !rule.IsInherited &&
            rule.AccessControlType == AccessControlType.Allow && current.Equals(rule.IdentityReference));
    }

    [SupportedOSPlatform("windows")]
    internal static DirectorySecurity PrivateDirectorySecurity()
    {
        SecurityIdentifier current = CurrentWindowsUserSid();
        var security = new DirectorySecurity();
        // The access token's default owner is not necessarily its user SID. In particular,
        // elevated/service accounts that belong to BUILTIN\Administrators commonly default new
        // objects to the Administrators group. IsVerifiedPrivateWindowsDirectory deliberately
        // requires the narrower user owner, so put it in the create-time descriptor instead of
        // assuming the token default. This also ensures only this identity has the owner's
        // implicit WRITE_DAC right while plaintext occupies the staging tree.
        security.SetOwner(current);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCreateDirectory(string path, ref NativeSecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFile(string path, uint desiredAccess,
        FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetFileInformationByHandle(SafeFileHandle file, int informationClass,
        IntPtr information, uint bufferSize);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int NativeMkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    /// <summary>
    /// Creates plaintext staging with restrictive security in the create syscall. Existing-output
    /// metadata is captured separately and applied only after the verified private object moves;
    /// a restrictive destination DACL must not block publication or expose staging plaintext.
    /// </summary>
    internal static FileStream CreatePrivateStagingFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).Create(
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                1 << 16,
                FileOptions.SequentialScan,
                PrivateFileSecurity());
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1 << 16,
            Options = FileOptions.SequentialScan,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    internal static FileSecurity PrivateFileSecurity()
    {
        SecurityIdentifier current = CurrentWindowsUserSid();
        var security = new FileSecurity();
        security.SetOwner(current);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ??
            throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private sealed record DirectoryMetadata(FileAttributes Attributes,
        byte[]? WindowsDacl, UnixFileMode? UnixMode);

    private static DirectoryMetadata CaptureDirectoryMetadata(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (OperatingSystem.IsWindows())
        {
            DirectorySecurity security = new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access);
            return new DirectoryMetadata(attributes, security.GetSecurityDescriptorBinaryForm(), null);
        }
        return new DirectoryMetadata(attributes, null, File.GetUnixFileMode(path));
    }

    private static void ApplyDirectoryMetadata(string path, DirectoryMetadata metadata)
    {
        if (OperatingSystem.IsWindows())
        {
            // DACL last: a valid caller policy may deny this identity WriteAttributes. The object
            // is still protected by the staging DACL while attributes are restored.
            File.SetAttributes(path, metadata.Attributes);
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(metadata.WindowsDacl!, AccessControlSections.Access);
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }
        File.SetUnixFileMode(path, metadata.UnixMode!.Value);
        File.SetAttributes(path, metadata.Attributes);
    }

    /// <summary>
    /// Publishes a fully verified file over an explicit destination while retaining its portable
    /// attributes. Windows refuses to replace a ReadOnly destination, so clear only that bit for
    /// the move, restore it on failure, and apply the complete original attribute set on success.
    /// The staging file itself remains writable until it has moved, making cleanup reliable.
    /// </summary>
    private static void PublishVerifiedReplacement(string staging, string destination)
    {
        FileAttributes attributes = File.GetAttributes(destination);

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(destination) & PortableUnixFileModeMask;
            File.SetUnixFileMode(staging, mode);
            File.SetAttributes(staging, attributes);
            File.Move(staging, destination, overwrite: true);
            return;
        }

        byte[] dacl = new FileInfo(destination)
            .GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();

        bool clearedReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (clearedReadOnly)
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
        try
        {
            File.Move(staging, destination, overwrite: true);
        }
        catch
        {
            if (clearedReadOnly && File.Exists(destination))
                File.SetAttributes(destination, attributes);
            throw;
        }

        // The moved private staging DACL gives this process the rights needed to finish. Restore
        // attributes first, then install the caller's DACL as the final metadata operation; that
        // policy may deliberately deny WriteAttributes or WriteDac afterwards.
        File.SetAttributes(destination, attributes);
        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(dacl, AccessControlSections.Access);
        new FileInfo(destination).SetAccessControl(security);
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
        var pathRoot = new ArchivePathNode("");
        int entryCount = 0, pathNodeCount = 0;
        using var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            EnsureArchiveEntryCount(++entryCount);
            string entryDisplay = ShardHeader.Display(entry.Name);
            bool isDirectory = entry.EntryType == TarEntryType.Directory;
            bool isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
            if (!isDirectory && !isFile)
                throw new ShardDecodeException(
                    $"Archive entry '{entryDisplay}' has unsupported type {entry.EntryType}; only files and directories are accepted.");

            if (entry.Name.Contains('\\'))
                throw new ShardDecodeException(
                    $"Archive entry '{entryDisplay}' uses a non-portable path separator.");
            string archivePath = entry.Name;
            if (isDirectory)
                archivePath = archivePath.TrimEnd('/');
            if (archivePath.Length == 0 || archivePath.StartsWith('/'))
                throw new ShardDecodeException($"Archive entry '{entryDisplay}' is not a safe relative path.");
            string[] segments = archivePath.Split('/');
            if (segments.Length > MaxArchiveDepth)
                throw new ShardDecodeException(
                    $"Archive entry '{entryDisplay}' exceeds the maximum path depth of {MaxArchiveDepth}.");
            if (segments.Any(segment => !IsSafePathSegment(segment)))
                throw new ShardDecodeException($"Archive entry '{entryDisplay}' is not a portable, safe path.");

            // Reject exact duplicates and case/Unicode-normalization aliases. An archive is a
            // cross-platform transfer: A.txt/a.txt or composed/decomposed spellings would silently
            // overwrite or merge on common Windows/macOS filesystems even when decoding on Linux.
            ArchivePathNode pathNode = pathRoot;
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                string segment = segments[segmentIndex];
                if (!TryCanonicalizePortableArchiveSegment(segment, out string spelling, out string canonical))
                    throw new ShardDecodeException(
                        $"Archive entry '{entryDisplay}' contains a non-ASCII path segment that cannot be " +
                        "safely case/Unicode-normalized in the current invariant-globalization runtime.");
                if (!pathNode.Children.TryGetValue(canonical, out ArchivePathNode? child))
                {
                    EnsureArchivePathNodeCount(++pathNodeCount);
                    child = new ArchivePathNode(spelling);
                    pathNode.Children.Add(canonical, child);
                }
                else if (child.Spelling != spelling)
                {
                    throw new ShardDecodeException(
                        $"Archive path segment spellings '{ShardHeader.Display(child.Spelling)}' and " +
                        $"'{ShardHeader.Display(spelling)}' collide on a case-insensitive or " +
                        $"Unicode-normalizing filesystem (entry '{entryDisplay}').");
                }

                if (child.IsFile && segmentIndex < segments.Length - 1)
                    throw new ShardDecodeException(
                        $"Archive entry '{entryDisplay}' places content below an existing file path.");
                pathNode = child;
            }
            if (pathNode.IsExplicitEntry)
                throw new ShardDecodeException($"Archive contains duplicate entry '{entryDisplay}'.");
            if (isFile && pathNode.Children.Count > 0)
                throw new ShardDecodeException(
                    $"Archive entry '{entryDisplay}' replaces a directory path with a file.");
            pathNode.IsExplicitEntry = true;
            pathNode.IsFile = isFile;

            string target = Path.GetFullPath(Path.Combine(destBare, Path.Combine(segments)));
            if (!target.StartsWith(destPrefix, StringComparison.Ordinal) && target != destBare)
                throw new ShardDecodeException($"Archive entry '{entryDisplay}' escapes the destination directory.");

            if (isDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
    }

    internal static void EnsureArchiveEntryCount(int count)
    {
        if (count > MaxArchiveEntries)
            throw new ShardDecodeException(
                $"Archive contains more than {MaxArchiveEntries:N0} entries; refusing an unbounded inode/memory workload.");
    }

    internal static void EnsureArchivePathNodeCount(int count)
    {
        if (count > MaxArchivePathNodes)
            throw new ShardDecodeException(
                $"Archive contains more than {MaxArchivePathNodes:N0} distinct path components; " +
                "refusing an unbounded directory/in-memory index workload.");
    }

    private static readonly bool UnicodeCanonicalizationAvailable = DetectUnicodeCanonicalization();

    /// <summary>
    /// Produces the normalized spelling and portable collision key shared by archive creation and
    /// extraction. In invariant-globalization deployments .NET cannot promise Unicode NFC/case
    /// aliases will collapse consistently; ASCII remains well-defined, while non-ASCII is refused
    /// instead of letting an archive pass a security check that a different host interprets
    /// differently.
    /// </summary>
    internal static bool TryCanonicalizePortableArchiveSegment(string segment,
        out string normalizedSpelling, out string collisionKey) =>
        TryCanonicalizePortableArchiveSegment(segment, UnicodeCanonicalizationAvailable,
            out normalizedSpelling, out collisionKey);

    /// <summary>Testable policy core; production callers use the runtime-detecting overload.</summary>
    internal static bool TryCanonicalizePortableArchiveSegment(string segment,
        bool unicodeCanonicalizationAvailable, out string normalizedSpelling, out string collisionKey)
    {
        bool ascii = true;
        foreach (char c in segment)
            ascii &= c <= '\x7f';
        if (!ascii && !unicodeCanonicalizationAvailable)
        {
            normalizedSpelling = "";
            collisionKey = "";
            return false;
        }
        try
        {
            normalizedSpelling = ascii ? segment : segment.Normalize(NormalizationForm.FormC);
            collisionKey = normalizedSpelling.ToUpperInvariant();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            normalizedSpelling = "";
            collisionKey = "";
            return false;
        }
    }

    private static bool DetectUnicodeCanonicalization()
    {
        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)
            return false;
        string? environment = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        if (environment is not null &&
            (environment.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             environment.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return false;
        try
        {
            // In invariant mode only ASCII casing is guaranteed. This behavioral probe covers
            // hosts that selected the mode through a runtime mechanism not reflected as a switch.
            return "\u00e9".Normalize(NormalizationForm.FormC).ToUpperInvariant() == "\u00c9";
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Segment trie for portable collision checks. The previous implementation retained and
    /// rebuilt every full prefix with string.Join at every depth, turning a legal 100k-entry,
    /// 128-deep archive into hundreds of gigabytes of temporary strings. One normalized segment
    /// per distinct node makes both retained memory and work linear in actual path bytes.
    /// </summary>
    private sealed class ArchivePathNode(string spelling)
    {
        public string Spelling { get; } = spelling;
        public Dictionary<string, ArchivePathNode> Children { get; } = new(StringComparer.Ordinal);
        public bool IsExplicitEntry { get; set; }
        public bool IsFile { get; set; }
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

        // Win32 DOS device names are not files. Opening "<dir>\NUL" with FileMode.Create SUCCEEDS,
        // every byte written is discarded, and no file appears — while File.Exists is false, so
        // the collision check below never diverts, and the SHA-256 still matches because it is
        // computed over the source stream rather than read back. A crafted header naming NUL
        // therefore makes the decode report "SHA-256 verified" over a file that does not exist:
        // silent, total data loss dressed as success, which is exactly the outcome the whole
        // verify-don't-assume design exists to prevent. The check is unconditional rather than
        // OS-gated so a name is only accepted if it is a plain file on every platform, matching
        // the character rule above.
        if (!IsSafePathSegment(name))
            return FallbackFileName;

        return name;
    }

    private static readonly string[] DosDevices =
        ["CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
          "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
          // Win32 explicitly treats the ISO-8859-1 superscript digits as device-number
          // suffixes too: COM¹.txt is a console device, not an ordinary Unicode filename.
          "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"];

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

    internal static bool IsSafePathSegment(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Length > 255 ||
            Encoding.UTF8.GetByteCount(name) > 255 || name[^1] is '.' or ' ' || IsDosDeviceName(name))
            return false;
        foreach (char c in name)
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ')
                return false;
        return true;
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
        // An explicit -o that points to an existing directory means "put the file inside that
        // directory" — the user is naming a destination folder, not a file. Combine it with the
        // sanitised embedded filename so the restore creates a file, not attempts to overwrite
        // the directory itself (which fails with "Access to the path is denied").
        if (outputPath is not null)
        {
            if (Directory.Exists(outputPath))
                return Path.Combine(outputPath, SafeFileName(first.FileName));
            return outputPath;
        }

        string safe = SafeFileName(first.FileName);
        string outPath = Path.Combine(Environment.CurrentDirectory, safe);
        if (!PathOccupied(outPath))
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
            if (!PathOccupied(candidate))
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
        if (!File.Exists(first) &&
            (!Directory.Exists(first) || !Directory.EnumerateFileSystemEntries(first).Any()))
            return first;
        for (int n = 1; n < 10_000; n++)
        {
            string candidate = Path.Combine(Environment.CurrentDirectory,
                n == 1 ? $"{stem}.restored" : $"{stem}.restored-{n}");
            if (!File.Exists(candidate) &&
                (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any()))
                return candidate;
        }
        throw new ShardDecodeException(
            $"Cannot find a free extraction directory for '{stem}' — 10,000 variants are already in use. Pass -o explicitly.");
    }

    private static bool PathOccupied(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Best-effort removal of a directory's children without deleting the directory itself. On
    /// Windows this is used while a no-delete-sharing handle still pins the exact private root;
    /// the root is only removed non-recursively after that handle is closed.
    /// </summary>
    private static void TryDeleteDirectoryContents(string path)
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0)
                            TryDeleteDirectoryContents(entry);
                        TryDeleteEmptyDirectory(entry);
                    }
                    else
                    {
                        if (OperatingSystem.IsWindows() &&
                            (attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                        File.Delete(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                                 DirectoryNotFoundException or FileNotFoundException)
                {
                    // Best effort. The root remains leased, so failure cannot redirect cleanup to
                    // a replacement path; a private leftover is safer than an over-broad delete.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         DirectoryNotFoundException)
        {
            // The root may already have been published or removed.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         DirectoryNotFoundException)
        {
            // Never fall back to recursive deletion after a Windows lease is closed: another
            // process could replace the pathname in that window.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryCleanupVerifiedWindowsDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        SafeFileHandle? cleanupLease = null;
        try
        {
            cleanupLease = OpenWindowsDirectoryLease(path);
            if (!IsVerifiedPrivateWindowsDirectory(path))
                return;
            TryDeleteDirectoryContents(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         DirectoryNotFoundException)
        {
            return;
        }
        finally
        {
            cleanupLease?.Dispose();
        }

        TryDeleteEmptyDirectory(path);
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
            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort — verification already failed louder
        }
    }

    /// <summary>Original path: no cross-shard parity — every data image must be present.</summary>
    private sealed record DataCandidates(Dictionary<int, DecodedShard> ByIndex, HashSet<int> Conflicts);

    private static DataCandidates CollectDataCandidates(IEnumerable<DecodedShard> shards, int count)
    {
        var byIndex = new Dictionary<int, DecodedShard>();
        var conflicts = new HashSet<int>();
        foreach (var s in shards)
        {
            if (s.Header.IsParity || (uint)s.Header.Index >= (uint)count || conflicts.Contains(s.Header.Index))
                continue;
            if (!byIndex.TryGetValue(s.Header.Index, out DecodedShard? existing))
            {
                byIndex.Add(s.Header.Index, s);
                continue;
            }
            bool identical = existing.Header.PayloadLength == s.Header.PayloadLength &&
                existing.Header.PayloadCrc32 == s.Header.PayloadCrc32 &&
                existing.Payload.AsSpan().SequenceEqual(s.Payload);
            if (!identical)
            {
                byIndex.Remove(s.Header.Index);
                conflicts.Add(s.Header.Index);
            }
        }
        return new DataCandidates(byIndex, conflicts);
    }

    private static byte[][] CollectContiguous(ShardHeader first, DataCandidates candidates)
    {
        int missingCount = first.Count - candidates.ByIndex.Count;
        if (missingCount > 0)
        {
            string preview = MissingDataPreview(candidates.ByIndex, first.Count);
            string conflicts = candidates.Conflicts.Count == 0
                ? ""
                : $" {candidates.Conflicts.Count:N0} ordinal(s) had conflicting CRC-valid copies and were treated as missing.";
            throw new ShardDecodeException(
                $"'{ShardHeader.Display(first.FileName)}': missing image(s) {preview} of {first.Count:N0} " +
                $"({missingCount:N0} total).{conflicts} Capture them and decode again.");
        }

        if (candidates.ByIndex.Values.Sum(s => (long)s.Payload.Length) != first.TotalLength)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': reassembled length does not match expected {first.TotalLength:N0}.");

        // Allocate the Count-sized result only after the sparse set proves every ordinal present.
        var chunks = new byte[first.Count][];
        foreach ((int index, DecodedShard shard) in candidates.ByIndex)
            chunks[index] = shard.Payload;
        return chunks;
    }

    private static string MissingDataPreview(IReadOnlyDictionary<int, DecodedShard> present, int count)
    {
        const int maximum = 10;
        var missing = new List<int>(maximum);
        for (int i = 0; i < count && missing.Count < maximum; i++)
            if (!present.ContainsKey(i))
                missing.Add(i + 1);
        string result = string.Join(", ", missing);
        return count - present.Count > missing.Count ? result + ", ..." : result;
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
