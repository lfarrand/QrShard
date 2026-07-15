namespace QrShard;

/// <summary>
/// Forward error correction for the cell stream: the stream is split into Reed-Solomon
/// codewords whose symbols are interleaved across the image, so a localized blob of damage
/// (a cursor, a notification, compression artifacts) spreads thinly over many codewords
/// instead of overwhelming one.
///
/// Byte k of the interleaved buffer holds symbol k / cwCount of codeword k % cwCount;
/// codeword j carries the contiguous data slice [j*dataLen, (j+1)*dataLen).
/// </summary>
internal static class Fec
{
    public const int CodewordLength = 255;
    public const int MaxParity = 64;

    public static int DataLength(int parity) => CodewordLength - parity;

    /// <summary>
    /// Encodes and interleaves the stream into a cwCount*255-byte cell buffer.
    /// Sequential by design: callers already parallelize per image, and nested
    /// Parallel.For loops only add scheduler contention.
    /// </summary>
    public static byte[] Protect(byte[] stream, int parity, int cwCount)
    {
        int dataLen = DataLength(parity);
        if (stream.Length > (long)cwCount * dataLen)
            throw new ArgumentException("Stream exceeds the protected capacity.");

        var buffer = new byte[cwCount * CodewordLength];
        Span<byte> cw = stackalloc byte[CodewordLength];
        for (int j = 0; j < cwCount; j++)
        {
            for (int i = 0; i < dataLen; i++)
            {
                int src = j * dataLen + i;
                cw[i] = src < stream.Length ? stream[src] : (byte)0;
            }
            ReedSolomon.Encode(cw[..dataLen], cw[dataLen..]);
            for (int i = 0; i < CodewordLength; i++)
                buffer[i * cwCount + j] = cw[i];
        }
        return buffer;
    }

    /// <summary>
    /// De-interleaves and error-corrects a captured cell buffer back into the data stream.
    /// Returns false when any codeword is damaged beyond correction.
    /// </summary>
    public static bool TryRecover(byte[] buffer, int parity, int cwCount, out byte[] stream, out int correctedBytes)
    {
        int dataLen = DataLength(parity);
        var result = new byte[cwCount * dataLen];
        int corrected = 0, failures = 0;

        Span<byte> cw = stackalloc byte[CodewordLength];
        for (int j = 0; j < cwCount; j++)
        {
            for (int i = 0; i < CodewordLength; i++)
                cw[i] = buffer[i * cwCount + j];

            if (ReedSolomon.TryDecode(cw, parity, out int errors))
            {
                cw[..dataLen].CopyTo(result.AsSpan(j * dataLen));
                corrected += errors;
            }
            else
            {
                failures++;
            }
        }

        stream = result;
        correctedBytes = corrected;
        return failures == 0;
    }
}
