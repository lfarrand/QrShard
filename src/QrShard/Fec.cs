using System.Runtime.Intrinsics;

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
        var buffer = new byte[cwCount * CodewordLength];
        ProtectInto(stream, stream.Length, parity, cwCount, buffer);
        return buffer;
    }

    /// <summary>
    /// Pooled-buffer variant: writes into <paramref name="dest"/> (which may be longer than
    /// needed — every byte of the cwCount*255 region is overwritten). Only the first
    /// <paramref name="streamLength"/> bytes of <paramref name="stream"/> are payload;
    /// the remainder of each codeword is zero-padded.
    /// </summary>
    public static void ProtectInto(byte[] stream, int streamLength, int parity, int cwCount, byte[] dest)
    {
        int dataLen = DataLength(parity);
        if (streamLength > (long)cwCount * dataLen)
            throw new ArgumentException("Stream exceeds the protected capacity.");
        if (dest.Length < cwCount * CodewordLength)
            throw new ArgumentException("Destination buffer is too small.");

        Span<byte> cw = stackalloc byte[CodewordLength];
        for (int j = 0; j < cwCount; j++)
        {
            for (int i = 0; i < dataLen; i++)
            {
                int src = j * dataLen + i;
                cw[i] = src < streamLength ? stream[src] : (byte)0;
            }
            ReedSolomon.Encode(cw[..dataLen], cw[dataLen..]);
            for (int i = 0; i < CodewordLength; i++)
                dest[i * cwCount + j] = cw[i];
        }
    }

    /// <summary>
    /// De-interleaves and error-corrects a captured cell buffer back into the data stream.
    /// Returns false when any codeword is damaged beyond correction.
    /// </summary>
    public static bool TryRecover(byte[] buffer, int parity, int cwCount, out byte[] stream, out int correctedBytes)
    {
        stream = new byte[cwCount * DataLength(parity)];
        return TryRecoverInto(buffer, parity, cwCount, stream, out correctedBytes);
    }

    /// <summary>Pooled-buffer variant of <see cref="TryRecover"/>; dest may be longer than needed.</summary>
    public static bool TryRecoverInto(byte[] buffer, int parity, int cwCount, byte[] dest, out int correctedBytes)
    {
        int dataLen = DataLength(parity);
        if (dest.Length < cwCount * dataLen)
            throw new ArgumentException("Destination buffer is too small.");
        int corrected = 0, failures = 0;
        var cwScratch = new byte[CodewordLength];

        int j = 0;

        // SIMD fast path: the buffer is interleaved (byte k*cwCount+j is symbol k of codeword j),
        // so 16 consecutive codewords' symbols sit in 16 consecutive bytes. Compute all their
        // syndromes together — one Vector128 lane per codeword, multiplying every lane by the
        // constant α^i via nibble shuffles. Clean codewords (the vast majority) are then copied
        // straight out; only dirty lanes fall back to the scalar Berlekamp-Massey decoder.
        if (Vector128.IsHardwareAccelerated && parity > 0 && cwCount >= 16)
        {
            var tableLo = new Vector128<byte>[parity];
            var tableHi = new Vector128<byte>[parity];
            for (int i = 0; i < parity; i++)
                (tableLo[i], tableHi[i]) = Gf256.MulTables(Gf256.AlphaPower(i));
            var synd = new Vector128<byte>[parity];

            for (; j + 16 <= cwCount; j += 16)
            {
                Array.Clear(synd);
                for (int k = 0; k < CodewordLength; k++)
                {
                    var c = Vector128.Create<byte>(buffer.AsSpan(k * cwCount + j, 16));
                    for (int i = 0; i < parity; i++)
                        synd[i] = Gf256.MulVec(synd[i], tableLo[i], tableHi[i]) ^ c;
                }

                var dirty = Vector128<byte>.Zero;
                for (int i = 0; i < parity; i++)
                    dirty |= synd[i];

                for (int lane = 0; lane < 16; lane++)
                {
                    if (dirty.GetElement(lane) == 0)
                        CopyCleanCodeword(buffer, cwCount, j + lane, dataLen, dest);
                    else
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures);
                }
            }
        }

        for (; j < cwCount; j++)
            DecodeCodeword(buffer, parity, cwCount, j, dataLen, dest, cwScratch, ref corrected, ref failures);

        correctedBytes = corrected;
        return failures == 0;
    }

    private static void CopyCleanCodeword(byte[] buffer, int cwCount, int j, int dataLen, byte[] dest)
    {
        for (int i = 0; i < dataLen; i++)
            dest[j * dataLen + i] = buffer[i * cwCount + j];
    }

    private static void DecodeCodeword(byte[] buffer, int parity, int cwCount, int j, int dataLen, byte[] dest,
        byte[] cwScratch, ref int corrected, ref int failures)
    {
        for (int i = 0; i < CodewordLength; i++)
            cwScratch[i] = buffer[i * cwCount + j];

        if (ReedSolomon.TryDecode(cwScratch, parity, out int errors))
        {
            cwScratch.AsSpan(0, dataLen).CopyTo(dest.AsSpan(j * dataLen));
            corrected += errors;
        }
        else
        {
            failures++;
        }
    }
}
