using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>
/// Abstraction over the bytes being encoded, so the encoder can stream large files per-chunk
/// instead of holding them in memory. Implementations must support concurrent reads.
/// </summary>
internal interface IPayloadSource : IDisposable
{
    long Length { get; }

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
}

/// <summary>In-memory source — used for small files and for deflate-compressed payloads.</summary>
internal sealed class BytePayloadSource(byte[] data) : IPayloadSource
{
    public byte[] Data => data;

    public long Length => data.LongLength;

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

    public MappedPayloadSource(string path)
    {
        Length = new FileInfo(path).Length;
        if (Length == 0)
            throw new ArgumentException("Cannot map an empty file.", nameof(path));
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer;
    }

    public long Length { get; }

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        new ReadOnlySpan<byte>(_pointer + offset, destination.Length).CopyTo(destination);
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
