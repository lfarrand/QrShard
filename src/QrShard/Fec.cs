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
internal sealed class Fec(Gf256 gf, ReedSolomon reedSolomon)
{
    public const int CodewordLength = 255;
    public const int MaxParity = 64;

    public Fec() : this(new Gf256(), new ReedSolomon())
    {
    }

    public static int DataLength(int parity) => CodewordLength - parity;

    /// <summary>
    /// Encodes and interleaves the stream into a cwCount*255-byte cell buffer.
    /// Sequential by design: callers already parallelize per image, and nested
    /// Parallel.For loops only add scheduler contention.
    /// </summary>
    public byte[] Protect(byte[] stream, int parity, int cwCount)
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
    public void ProtectInto(byte[] stream, int streamLength, int parity, int cwCount, byte[] dest)
    {
        int dataLen = DataLength(parity);
        if (streamLength > (long)cwCount * dataLen)
            throw new ArgumentException("Stream exceeds the protected capacity.");
        if (dest.Length < cwCount * CodewordLength)
            throw new ArgumentException("Destination buffer is too small.");

        int j = 0;

        // SIMD fast path, mirroring the decode-side syndrome scan: encode 16 codewords at
        // once, one Vector128 lane per codeword. The LFSR step multiplies every lane's
        // feedback coefficient by the FIXED generator coefficient gen[k+1], which is exactly
        // the nibble-shuffle-table case. The interleaved output layout also turns the
        // write-out into contiguous 16-byte stores.
        if (Vector128.IsHardwareAccelerated && parity > 0 && cwCount >= 16)
        {
            byte[] genTail = reedSolomon.GeneratorTail(parity);
            var tableLo = new Vector128<byte>[parity];
            var tableHi = new Vector128<byte>[parity];
            for (int i = 0; i < parity; i++)
                (tableLo[i], tableHi[i]) = gf.MulTables(genTail[i]);
            var register = new Vector128<byte>[parity];
            Span<byte> lanes = stackalloc byte[16];

            for (; j + 16 <= cwCount; j += 16)
            {
                Array.Clear(register);
                for (int i = 0; i < dataLen; i++)
                {
                    for (int lane = 0; lane < 16; lane++)
                    {
                        int src = (j + lane) * dataLen + i;
                        lanes[lane] = src < streamLength ? stream[src] : (byte)0;
                    }
                    var d = Vector128.Create<byte>(lanes);
                    d.CopyTo(dest.AsSpan(i * cwCount + j, 16));

                    var coef = d ^ register[0];
                    for (int k = 0; k < parity - 1; k++)
                        register[k] = register[k + 1];
                    register[parity - 1] = Vector128<byte>.Zero;
                    for (int k = 0; k < parity; k++)
                        register[k] ^= gf.MulVec(coef, tableLo[k], tableHi[k]);
                }
                for (int i = 0; i < parity; i++)
                    register[i].CopyTo(dest.AsSpan((dataLen + i) * cwCount + j, 16));
            }
        }

        Span<byte> cw = stackalloc byte[CodewordLength];
        for (; j < cwCount; j++)
        {
            for (int i = 0; i < dataLen; i++)
            {
                int src = j * dataLen + i;
                cw[i] = src < streamLength ? stream[src] : (byte)0;
            }
            reedSolomon.Encode(cw[..dataLen], cw[dataLen..]);
            for (int i = 0; i < CodewordLength; i++)
                dest[i * cwCount + j] = cw[i];
        }
    }

    /// <summary>
    /// De-interleaves and error-corrects a captured cell buffer back into the data stream.
    /// Returns false when any codeword is damaged beyond correction.
    /// </summary>
    public bool TryRecover(byte[] buffer, int parity, int cwCount, out byte[] stream, out int correctedBytes)
    {
        stream = new byte[cwCount * DataLength(parity)];
        return TryRecoverInto(buffer, parity, cwCount, stream, out correctedBytes);
    }

    /// <summary>
    /// Pooled-buffer variant of <see cref="TryRecover"/>; dest may be longer than needed.
    /// When <paramref name="codewordErrors"/> is supplied (length cwCount), each entry receives
    /// that codeword's corrected-byte count, or -1 if it was damaged beyond correction — the
    /// data behind the diagnostic heatmap.
    /// </summary>
    /// <param name="suspectBytes">
    /// Optional per-byte suspicion flags in interleaved indexing (byte k = symbol k/cwCount of
    /// codeword k%cwCount) — cells whose color classification was ambiguous. Flagged symbols
    /// are retried as ERASURES when errors-only decoding fails: RS corrects 2x as many known
    /// positions as unknown ones, so borderline captures gain real capacity.
    /// </param>
    public bool TryRecoverInto(byte[] buffer, int parity, int cwCount, byte[] dest, out int correctedBytes,
        int[]? codewordErrors = null, bool[]? suspectBytes = null)
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
                (tableLo[i], tableHi[i]) = gf.MulTables(gf.AlphaPower(i));
            var synd = new Vector128<byte>[parity];

            for (; j + 16 <= cwCount; j += 16)
            {
                Array.Clear(synd);
                for (int k = 0; k < CodewordLength; k++)
                {
                    var c = Vector128.Create<byte>(buffer.AsSpan(k * cwCount + j, 16));
                    for (int i = 0; i < parity; i++)
                        synd[i] = gf.MulVec(synd[i], tableLo[i], tableHi[i]) ^ c;
                }

                var dirty = Vector128<byte>.Zero;
                for (int i = 0; i < parity; i++)
                    dirty |= synd[i];

                for (int lane = 0; lane < 16; lane++)
                {
                    if (dirty.GetElement(lane) == 0)
                        CopyCleanCodeword(buffer, cwCount, j + lane, dataLen, dest);
                    else
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes);
                }
            }
        }

        for (; j < cwCount; j++)
            DecodeCodeword(buffer, parity, cwCount, j, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes);

        correctedBytes = corrected;
        return failures == 0;
    }

    private static void CopyCleanCodeword(byte[] buffer, int cwCount, int j, int dataLen, byte[] dest)
    {
        for (int i = 0; i < dataLen; i++)
            dest[j * dataLen + i] = buffer[i * cwCount + j];
    }

    /// <summary>
    /// Multi-capture recovery: several captures of the SAME shard, each individually damaged
    /// beyond correction, are decoded codeword by codeword — each codeword takes the first
    /// capture whose copy corrects, falling back to a per-byte majority vote across captures.
    /// Glare or reflections rarely sit in the same place twice, so photos that fail alone
    /// often succeed together.
    /// </summary>
    public bool TryRecoverFused(IReadOnlyList<byte[]> buffers, int parity, int cwCount, byte[] dest, out int correctedBytes)
    {
        int dataLen = DataLength(parity);
        if (dest.Length < cwCount * dataLen)
            throw new ArgumentException("Destination buffer is too small.");
        correctedBytes = 0;
        var cw = new byte[CodewordLength];

        for (int j = 0; j < cwCount; j++)
        {
            bool solved = false;
            foreach (byte[] buffer in buffers)
            {
                for (int i = 0; i < CodewordLength; i++)
                    cw[i] = buffer[i * cwCount + j];
                if (reedSolomon.TryDecode(cw, parity, out int errors))
                {
                    cw.AsSpan(0, dataLen).CopyTo(dest.AsSpan(j * dataLen));
                    correctedBytes += errors;
                    solved = true;
                    break;
                }
            }
            if (solved)
                continue;

            if (buffers.Count >= 3)
            {
                for (int i = 0; i < CodewordLength; i++)
                    cw[i] = MajorityByte(buffers, i * cwCount + j);
                if (reedSolomon.TryDecode(cw, parity, out int errors))
                {
                    cw.AsSpan(0, dataLen).CopyTo(dest.AsSpan(j * dataLen));
                    correctedBytes += errors;
                    continue;
                }
            }
            return false;
        }
        return true;
    }

    private static byte MajorityByte(IReadOnlyList<byte[]> buffers, int index)
    {
        // Tiny N (a handful of captures): count agreements pairwise.
        byte best = buffers[0][index];
        int bestVotes = 1;
        for (int a = 0; a < buffers.Count; a++)
        {
            byte candidate = buffers[a][index];
            int votes = 0;
            for (int b = 0; b < buffers.Count; b++)
                if (buffers[b][index] == candidate)
                    votes++;
            if (votes > bestVotes)
            {
                bestVotes = votes;
                best = candidate;
            }
        }
        return best;
    }

    private void DecodeCodeword(byte[] buffer, int parity, int cwCount, int j, int dataLen, byte[] dest,
        byte[] cwScratch, ref int corrected, ref int failures, int[]? codewordErrors, bool[]? suspectBytes)
    {
        for (int i = 0; i < CodewordLength; i++)
            cwScratch[i] = buffer[i * cwCount + j];

        // Errors-only first: it is the proven path and erasure marks can be wrong (a wrongly
        // flagged good symbol eats capacity). Only when that fails do the flags get their say.
        if (reedSolomon.TryDecode(cwScratch, parity, out int errors))
        {
            cwScratch.AsSpan(0, dataLen).CopyTo(dest.AsSpan(j * dataLen));
            corrected += errors;
            if (codewordErrors is not null)
                codewordErrors[j] = errors;
            return;
        }

        if (suspectBytes is not null && TryErasureRetry(buffer, parity, cwCount, j, cwScratch, suspectBytes, out errors))
        {
            cwScratch.AsSpan(0, dataLen).CopyTo(dest.AsSpan(j * dataLen));
            corrected += errors;
            if (codewordErrors is not null)
                codewordErrors[j] = errors;
            return;
        }

        failures++;
        if (codewordErrors is not null)
            codewordErrors[j] = -1;
    }

    private bool TryErasureRetry(byte[] buffer, int parity, int cwCount, int j, byte[] cwScratch,
        bool[] suspectBytes, out int errors)
    {
        errors = 0;

        // Collect this codeword's flagged symbols. Leave slack for one unmarked error
        // (2·1 + f ≤ parity); more flags than that means the marks are useless here.
        Span<int> erasures = stackalloc int[MaxParity];
        int f = 0;
        int limit = parity - 2;
        for (int i = 0; i < CodewordLength; i++)
        {
            int idx = i * cwCount + j;
            if (idx >= suspectBytes.Length || !suspectBytes[idx])
                continue;
            if (f == limit)
                return false; // over-flagged codeword — erasure info not usable
            erasures[f++] = i;
        }
        if (f == 0)
            return false;

        // The failed errors-only attempt may have scribbled on the scratch — re-extract.
        for (int i = 0; i < CodewordLength; i++)
            cwScratch[i] = buffer[i * cwCount + j];
        return reedSolomon.TryDecodeWithErasures(cwScratch, parity, erasures[..f], out errors);
    }
}
