using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace QrShard;

internal enum MappedPayloadSourceConstructionStage
{
    FileCreated,
    ViewCreated,
    PointerAcquired,
}

/// <summary>
/// Abstraction over the bytes being encoded, so the encoder can stream large files per-chunk
/// instead of holding them in memory. Implementations must support concurrent reads.
/// </summary>
internal interface IPayloadSource : IDisposable
{
    long Length { get; }

    /// <summary>Managed bytes retained for the life of this source (a memory map is zero).</summary>
    long ResidentBytes { get; }

    /// <summary>Copies <c>destination.Length</c> bytes starting at <paramref name="offset"/>.</summary>
    void Read(long offset, Span<byte> destination);
}

internal static class PayloadSource
{
    /// <summary>Streaming SHA-256 over any source, in bounded chunks.</summary>
    public static byte[] ComputeSha256(IPayloadSource source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        try
        {
            long remaining = source.Length, offset = 0;
            while (remaining > 0)
            {
                int n = (int)Math.Min(buffer.Length, remaining);
                source.Read(offset, buffer.AsSpan(0, n));
                hash.AppendData(buffer, 0, n);
                offset += n;
                remaining -= n;
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            // This buffer contains source plaintext even when the prepared payload will be
            // password-encrypted. Do not leave its final chunk in managed memory after hashing.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

/// <summary>In-memory source — used for small files and for Brotli-compressed payloads.</summary>
internal sealed class BytePayloadSource(byte[] data) : IPayloadSource
{
    public byte[] Data => data;

    public long Length => data.LongLength;
    public long ResidentBytes => data.LongLength;

    public void Read(long offset, Span<byte> destination) =>
        data.AsSpan((int)offset, destination.Length).CopyTo(destination);

    public void Dispose()
    {
    }
}

/// <summary>
/// Memory-mapped file source: the encoder's parallel workers each read their own chunk
/// directly from the mapping, so a large incompressible file (the common big-transfer case —
/// zips, media) is never materialized as one giant managed array.
/// </summary>
internal sealed unsafe class MappedPayloadSource : IPayloadSource
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _pointer;
    private int disposed;

    public MappedPayloadSource(string path) : this(path, null)
    {
    }

    /// <summary>
    /// The checkpoint is an internal failure-injection seam used to verify cleanup after each
    /// native resource acquisition. Production callers always use the one-argument constructor.
    /// </summary>
    internal MappedPayloadSource(string path,
        Action<MappedPayloadSourceConstructionStage, MemoryMappedFile,
            MemoryMappedViewAccessor?>? checkpoint)
    {
        Length = new FileInfo(path).Length;
        if (Length == 0)
            throw new ArgumentException("Cannot map an empty file.", nameof(path));
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        byte* pointer = null;
        bool pointerAcquired = false;
        try
        {
            file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0,
                MemoryMappedFileAccess.Read);
            checkpoint?.Invoke(MappedPayloadSourceConstructionStage.FileCreated, file, null);
            view = file.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
            checkpoint?.Invoke(MappedPayloadSourceConstructionStage.ViewCreated, file, view);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointerAcquired = true;
            checkpoint?.Invoke(MappedPayloadSourceConstructionStage.PointerAcquired, file, view);
        }
        catch
        {
            try
            {
                if (pointerAcquired)
                    view!.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            finally
            {
                try
                {
                    view?.Dispose();
                }
                finally
                {
                    file?.Dispose();
                }
            }
            throw;
        }

        _file = file;
        _view = view!;
        _pointer = pointer;
    }

    public long Length { get; }
    public long ResidentBytes => 0;

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        new ReadOnlySpan<byte>(_pointer + offset, destination.Length).CopyTo(destination);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        finally
        {
            try
            {
                _view.Dispose();
            }
            finally
            {
                _file.Dispose();
            }
        }
    }
}
