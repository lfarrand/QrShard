namespace QrShard;

/// <summary>One successfully decoded shard image: its header, verified payload, and provenance.</summary>
internal sealed record DecodedShard(ShardHeader Header, byte[] Payload, string SourceFile, int EccParity, int CorrectedBytes);

/// <summary>One file written by a decode run.</summary>
internal sealed record RestoredFile(string FileName, string OutputPath, long Length);

/// <summary>Any failure while decoding a capture; the message is user-facing and actionable.</summary>
internal sealed class ShardDecodeException(string message) : Exception(message);

/// <summary>
/// Diagnostic capture of a single-image decode attempt: the located layout, per-codeword ECC
/// correction counts (0 = clean, -1 = uncorrectable), and the outcome.
/// </summary>
internal sealed class DecodeDiagnostics
{
    public Layout? Layout { get; set; }
    public int[] CodewordErrors { get; set; } = [];
    public DecodedShard? Shard { get; set; }
    public string? Error { get; set; }
}
