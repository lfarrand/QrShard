using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

        // GFNI wide encode: 64/32 codewords per block through the LFSR, one affine transform
        // per generator coefficient per step (mirrors the decode-side wide syndrome scan).
        if (Gfni.V512.IsSupported && parity > 0 && cwCount - j >= 64)
        {
            byte[] genTail = reedSolomon.GeneratorTail(parity);
            var matrices = new Vector512<byte>[parity];
            for (int i = 0; i < parity; i++)
                matrices[i] = Vector512.Create(gf.AffineMatrix(genTail[i])).AsByte();
            var register = new Vector512<byte>[parity];
            Span<byte> lanes = stackalloc byte[64];

            for (; j + 64 <= cwCount; j += 64)
            {
                Array.Clear(register);
                for (int i = 0; i < dataLen; i++)
                {
                    for (int lane = 0; lane < 64; lane++)
                    {
                        int src = (j + lane) * dataLen + i;
                        lanes[lane] = src < streamLength ? stream[src] : (byte)0;
                    }
                    var d = Vector512.Create<byte>(lanes);
                    d.CopyTo(dest.AsSpan(i * cwCount + j, 64));

                    var coef = d ^ register[0];
                    for (int k = 0; k < parity - 1; k++)
                        register[k] = register[k + 1];
                    register[parity - 1] = Vector512<byte>.Zero;
                    for (int k = 0; k < parity; k++)
                        register[k] ^= Gfni.V512.GaloisFieldAffineTransform(coef, matrices[k], 0);
                }
                for (int i = 0; i < parity; i++)
                    register[i].CopyTo(dest.AsSpan((dataLen + i) * cwCount + j, 64));
            }
        }
        if (Gfni.V256.IsSupported && parity > 0 && cwCount - j >= 32)
        {
            byte[] genTail = reedSolomon.GeneratorTail(parity);
            var matrices = new Vector256<byte>[parity];
            for (int i = 0; i < parity; i++)
                matrices[i] = Vector256.Create(gf.AffineMatrix(genTail[i])).AsByte();
            var register = new Vector256<byte>[parity];
            Span<byte> lanes = stackalloc byte[32];

            for (; j + 32 <= cwCount; j += 32)
            {
                Array.Clear(register);
                for (int i = 0; i < dataLen; i++)
                {
                    for (int lane = 0; lane < 32; lane++)
                    {
                        int src = (j + lane) * dataLen + i;
                        lanes[lane] = src < streamLength ? stream[src] : (byte)0;
                    }
                    var d = Vector256.Create<byte>(lanes);
                    d.CopyTo(dest.AsSpan(i * cwCount + j, 32));

                    var coef = d ^ register[0];
                    for (int k = 0; k < parity - 1; k++)
                        register[k] = register[k + 1];
                    register[parity - 1] = Vector256<byte>.Zero;
                    for (int k = 0; k < parity; k++)
                        register[k] ^= Gfni.V256.GaloisFieldAffineTransform(coef, matrices[k], 0);
                }
                for (int i = 0; i < parity; i++)
                    register[i].CopyTo(dest.AsSpan((dataLen + i) * cwCount + j, 32));
            }
        }

        // No GFNI, but AVX2: the same LFSR over 32 codewords per block (mirrors the decode-side
        // AVX2 syndrome scan).
        if (Avx2.IsSupported && parity > 0 && cwCount - j >= 32)
        {
            byte[] genTail = reedSolomon.GeneratorTail(parity);
            var tableLo = new Vector256<byte>[parity];
            var tableHi = new Vector256<byte>[parity];
            for (int i = 0; i < parity; i++)
                (tableLo[i], tableHi[i]) = gf.MulTables256(genTail[i]);
            var register = new Vector256<byte>[parity];
            Span<byte> lanes = stackalloc byte[32];

            for (; j + 32 <= cwCount; j += 32)
            {
                Array.Clear(register);
                for (int i = 0; i < dataLen; i++)
                {
                    for (int lane = 0; lane < 32; lane++)
                    {
                        int src = (j + lane) * dataLen + i;
                        lanes[lane] = src < streamLength ? stream[src] : (byte)0;
                    }
                    var d = Vector256.Create<byte>(lanes);
                    d.CopyTo(dest.AsSpan(i * cwCount + j, 32));

                    var coef = d ^ register[0];
                    for (int k = 0; k < parity - 1; k++)
                        register[k] = register[k + 1];
                    register[parity - 1] = Vector256<byte>.Zero;
                    for (int k = 0; k < parity; k++)
                        register[k] ^= gf.MulVec256(coef, tableLo[k], tableHi[k]);
                }
                for (int i = 0; i < parity; i++)
                    register[i].CopyTo(dest.AsSpan((dataLen + i) * cwCount + j, 32));
            }
        }

        // SIMD fast path, mirroring the decode-side syndrome scan: encode 16 codewords at
        // once, one Vector128 lane per codeword. The LFSR step multiplies every lane's
        // feedback coefficient by the FIXED generator coefficient gen[k+1], which is exactly
        // the nibble-shuffle-table case. The interleaved output layout also turns the
        // write-out into contiguous 16-byte stores.
        if (Vector128.IsHardwareAccelerated && parity > 0 && cwCount - j >= 16)
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
    /// <param name="stopAfterFirstFailure">
    /// Returns as soon as one codeword is unrecoverable. Fusion hypotheses use this because a
    /// single failed codeword invalidates the whole candidate; normal decode leaves it false so
    /// diagnostics and correction totals cover every codeword.
    /// </param>
    public bool TryRecoverInto(byte[] buffer, int parity, int cwCount, byte[] dest, out int correctedBytes,
        int[]? codewordErrors = null, bool[]? suspectBytes = null, byte[]? secondChoice = null,
        bool stopAfterFirstFailure = false)
    {
        int dataLen = DataLength(parity);
        if (dest.Length < cwCount * dataLen)
            throw new ArgumentException("Destination buffer is too small.");
        int corrected = 0, failures = 0;
        var cwScratch = new byte[CodewordLength];

        int j = 0;

        // GFNI wide paths: syndromes for 64 (AVX-512) or 32 (AVX) codewords per block, one
        // GF2P8AFFINEQB per α-power per step — multiplication by the fixed α^i is a GF(2)
        // bit-matrix, which is exactly what the affine instruction applies per byte.
        if (Gfni.V512.IsSupported && parity > 0 && cwCount - j >= 64)
        {
            var matrices = new Vector512<byte>[parity];
            for (int i = 0; i < parity; i++)
                matrices[i] = Vector512.Create(gf.AffineMatrix(gf.AlphaPower(i))).AsByte();
            var synd = new Vector512<byte>[parity];

            for (; j + 64 <= cwCount; j += 64)
            {
                Array.Clear(synd);
                for (int k = 0; k < CodewordLength; k++)
                {
                    var c = Vector512.Create<byte>(buffer.AsSpan(k * cwCount + j, 64));
                    for (int i = 0; i < parity; i++)
                        synd[i] = Gfni.V512.GaloisFieldAffineTransform(synd[i], matrices[i], 0) ^ c;
                }

                var dirty = Vector512<byte>.Zero;
                for (int i = 0; i < parity; i++)
                    dirty |= synd[i];

                for (int lane = 0; lane < 64; lane++)
                {
                    if (dirty.GetElement(lane) == 0)
                        CopyCleanCodeword(buffer, cwCount, j + lane, dataLen, dest);
                    else
                    {
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes, secondChoice);
                        if (stopAfterFirstFailure && failures > 0)
                        {
                            correctedBytes = corrected;
                            return false;
                        }
                    }
                }
            }
        }
        if (Gfni.V256.IsSupported && parity > 0 && cwCount - j >= 32)
        {
            var matrices = new Vector256<byte>[parity];
            for (int i = 0; i < parity; i++)
                matrices[i] = Vector256.Create(gf.AffineMatrix(gf.AlphaPower(i))).AsByte();
            var synd = new Vector256<byte>[parity];

            for (; j + 32 <= cwCount; j += 32)
            {
                Array.Clear(synd);
                for (int k = 0; k < CodewordLength; k++)
                {
                    var c = Vector256.Create<byte>(buffer.AsSpan(k * cwCount + j, 32));
                    for (int i = 0; i < parity; i++)
                        synd[i] = Gfni.V256.GaloisFieldAffineTransform(synd[i], matrices[i], 0) ^ c;
                }

                var dirty = Vector256<byte>.Zero;
                for (int i = 0; i < parity; i++)
                    dirty |= synd[i];

                for (int lane = 0; lane < 32; lane++)
                {
                    if (dirty.GetElement(lane) == 0)
                        CopyCleanCodeword(buffer, cwCount, j + lane, dataLen, dest);
                    else
                    {
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes, secondChoice);
                        if (stopAfterFirstFailure && failures > 0)
                        {
                            correctedBytes = corrected;
                            return false;
                        }
                    }
                }
            }
        }

        // No GFNI, but AVX2: same syndrome scan over 32 codewords per block via the nibble
        // shuffle. Worth its own tier because this loop is the decode side's compute floor —
        // parity accumulators live in registers, so ~parity*7 ALU ops ride on every 32-byte
        // load and doubling the lane count converts almost 1:1 into throughput.
        if (Avx2.IsSupported && parity > 0 && cwCount - j >= 32)
        {
            var tableLo = new Vector256<byte>[parity];
            var tableHi = new Vector256<byte>[parity];
            for (int i = 0; i < parity; i++)
                (tableLo[i], tableHi[i]) = gf.MulTables256(gf.AlphaPower(i));
            var synd = new Vector256<byte>[parity];

            for (; j + 32 <= cwCount; j += 32)
            {
                Array.Clear(synd);
                for (int k = 0; k < CodewordLength; k++)
                {
                    var c = Vector256.Create<byte>(buffer.AsSpan(k * cwCount + j, 32));
                    for (int i = 0; i < parity; i++)
                        synd[i] = gf.MulVec256(synd[i], tableLo[i], tableHi[i]) ^ c;
                }

                var dirty = Vector256<byte>.Zero;
                for (int i = 0; i < parity; i++)
                    dirty |= synd[i];

                for (int lane = 0; lane < 32; lane++)
                {
                    if (dirty.GetElement(lane) == 0)
                        CopyCleanCodeword(buffer, cwCount, j + lane, dataLen, dest);
                    else
                    {
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes, secondChoice);
                        if (stopAfterFirstFailure && failures > 0)
                        {
                            correctedBytes = corrected;
                            return false;
                        }
                    }
                }
            }
        }

        // SIMD fast path: the buffer is interleaved (byte k*cwCount+j is symbol k of codeword j),
        // so 16 consecutive codewords' symbols sit in 16 consecutive bytes. Compute all their
        // syndromes together — one Vector128 lane per codeword, multiplying every lane by the
        // constant α^i via nibble shuffles. Clean codewords (the vast majority) are then copied
        // straight out; only dirty lanes fall back to the scalar Berlekamp-Massey decoder.
        if (Vector128.IsHardwareAccelerated && parity > 0 && cwCount - j >= 16)
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
                    {
                        DecodeCodeword(buffer, parity, cwCount, j + lane, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes, secondChoice);
                        if (stopAfterFirstFailure && failures > 0)
                        {
                            correctedBytes = corrected;
                            return false;
                        }
                    }
                }
            }
        }

        for (; j < cwCount; j++)
        {
            DecodeCodeword(buffer, parity, cwCount, j, dataLen, dest, cwScratch, ref corrected, ref failures, codewordErrors, suspectBytes, secondChoice);
            if (stopAfterFirstFailure && failures > 0)
            {
                correctedBytes = corrected;
                return false;
            }
        }

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
        // Capture count is bounded by PhotoFusion, but a fixed byte histogram is both simpler and
        // linear in that count instead of comparing every capture with every other capture.
        Span<byte> votes = stackalloc byte[256];
        votes.Clear();
        byte best = 0;
        byte bestVotes = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            byte candidate = buffers[i][index];
            byte count = ++votes[candidate];
            if (count > bestVotes)
            {
                bestVotes = count;
                best = candidate;
            }
        }
        return best;
    }

    private void DecodeCodeword(byte[] buffer, int parity, int cwCount, int j, int dataLen, byte[] dest,
        byte[] cwScratch, ref int corrected, ref int failures, int[]? codewordErrors, bool[]? suspectBytes,
        byte[]? secondChoice)
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

        if (suspectBytes is not null && secondChoice is not null
            && TryChase(buffer, secondChoice, parity, cwCount, j, cwScratch, suspectBytes, out errors))
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

    /// <summary>Exhaustive Chase subsets over at most this many ambiguous symbols (2^6 - 1 trials).</summary>
    private const int MaxChasePositions = 6;

    /// <summary>
    /// Below this parity the all-flip branch does NOT get its un-flip subset search. Extra shots at
    /// the decoding bound are only safe when there are syndromes to spare: at parity 8 the same
    /// search turns 109 false accepts into 2706 over 3000 past-capacity codewords, and returns more
    /// wrong answers than right ones. At 16 and above it produced neither, across both arms.
    /// </summary>
    private const int UnflipMinParity = 16;

    /// <summary>
    /// Chase decoding: the classifier recorded each ambiguous cell's runner-up value, so trial
    /// codewords splice second choices over the received symbols. Few ambiguous symbols get an
    /// exhaustive subset search; many (systematic blur — best/second swapped across a whole
    /// region) get a single all-flipped trial, which is exactly the codeword the classifier
    /// WOULD have produced had every coin-flip landed the other way.
    /// </summary>
    private bool TryChase(byte[] buffer, byte[] secondChoice, int parity, int cwCount, int j, byte[] cwScratch,
        bool[] suspectBytes, out int errors)
    {
        errors = 0;
        Span<int> positions = stackalloc int[MaxChasePositions];
        int count = 0;
        bool overflow = false;
        for (int i = 0; i < CodewordLength; i++)
        {
            int idx = i * cwCount + j;
            if (idx >= suspectBytes.Length || !suspectBytes[idx])
                continue;
            if (idx >= secondChoice.Length || buffer[idx] == secondChoice[idx])
                continue; // flagged, but the alternative byte is identical — nothing to try
            if (count == MaxChasePositions)
            {
                overflow = true;
                break;
            }
            positions[count++] = i;
        }
        if (count == 0)
            return false;

        if (overflow)
        {
            // All-flipped trial: every differing ambiguous symbol takes its second choice.
            for (int i = 0; i < CodewordLength; i++)
            {
                int idx = i * cwCount + j;
                cwScratch[i] = idx < suspectBytes.Length && suspectBytes[idx]
                               && idx < secondChoice.Length && buffer[idx] != secondChoice[idx]
                    ? secondChoice[idx]
                    : buffer[idx];
            }
            if (reedSolomon.TryDecode(cwScratch, parity, out _))
            {
                errors = SymbolsChanged(buffer, cwScratch, cwCount, j);
                return true;
            }

            // All-flip assumes EVERY ambiguous cell was misread. That is the case its author
            // pictured — systematic blur swapping best and second across a whole region — and for
            // it the single trial is optimal. The neighbouring case, most but not all misread, got
            // nothing: flipping the ones that were already right introduces fresh errors, and if
            // that overshoot pushes the count past t = parity/2 the codeword is simply lost.
            //
            // Un-flipping only has to get back UNDER the bound, not undo every mistake. With the
            // overshoot at t+1 a single un-flip suffices, so a subset search over the first six
            // flagged positions lands often enough to matter, and needs no reliability ordering
            // to aim it. Same trial budget as the exhaustive branch, so the worst case is
            // unchanged at 63 decodes.
            //
            // Gated hard at parity 16, because below it the extra shots at the bound are not
            // free. Measured over 3000 trials per parity, in the regime where all-flip has just
            // failed — "recoverable" carries a truth that a correct decoder could reach,
            // "unrecoverable" is past capacity so every acceptance is a miscorrection:
            //
            //     parity                 4      8     12     16     32
            //     rescued              212   1636   2602   2763   2949
            //     WRONG answers       2788   1329     55      0      0
            //     false accepts   1471->3000  109->2706  5->216  0->0  0->0
            //
            // At 4 and 8 it is catastrophic; at 12 it is a trade; at 16 and above it is free, and
            // 16 is the shipped default for EncodeDefaults.EccParity.
            if (parity < UnflipMinParity)
                return false;
            Span<byte> unflipBest = stackalloc byte[CodewordLength];
            int unflipWeight = int.MaxValue;
            for (int mask = 1; mask < 1 << count; mask++)
            {
                for (int i = 0; i < CodewordLength; i++)
                {
                    int idx = i * cwCount + j;
                    cwScratch[i] = idx < suspectBytes.Length && suspectBytes[idx]
                                   && idx < secondChoice.Length && buffer[idx] != secondChoice[idx]
                        ? secondChoice[idx]
                        : buffer[idx];
                }
                // Put the received value back at the chosen positions: the classifier's first
                // choice was right there and all-flip had broken it.
                for (int b = 0; b < count; b++)
                    if ((mask & (1 << b)) != 0)
                        cwScratch[positions[b]] = buffer[positions[b] * cwCount + j];
                if (reedSolomon.TryDecode(cwScratch, parity, out int weight) && weight < unflipWeight)
                {
                    unflipWeight = weight;
                    cwScratch.AsSpan(0, CodewordLength).CopyTo(unflipBest);
                }
            }
            if (unflipWeight == int.MaxValue)
                return false;
            unflipBest.CopyTo(cwScratch);
            errors = SymbolsChanged(buffer, cwScratch, cwCount, j);
            return true;
        }

        // Take the candidate the decoder was most CONFIDENT about, not the first one that happens
        // to verify. Several trial patterns commonly produce valid codewords past the errors-only
        // bound, and ascending mask order ranks them by nothing meaningful — mask 3 (flip 0 and 1)
        // is tried before mask 4 (flip 2 alone).
        //
        // The score is the weight of corrections REED-SOLOMON had to make, which TryDecode already
        // returns: a candidate that spent less of its correction budget sits deeper inside its
        // decoding sphere and is far less likely to be a miscorrection. Crucially it is NOT the
        // distance from the received word, which is what a literal reading of Chase-II's arg-min
        // suggests. That metric conflates two different things — the splices are HYPOTHESES the
        // decoder chose, the corrections are EVIDENCE of error — and scoring on it is actively
        // worse than taking the first hit. Measured over 6000 trials per parity, in the regime
        // where the exhaustive branch actually runs:
        //
        //     scored by            parity 8      parity 12     parity 16
        //     distance-from-received  +64 / -583    +6 / -52     +0 / -2
        //     correction weight     +1012 / -0     +39 / -0     +1 / -0
        //
        // (fixed / broken against first-match). Chase-II weights its distance by per-symbol
        // reliability for exactly this reason; discounting the flipped positions entirely is the
        // hard-decision limit of that idea, and it is the version this codebase can compute,
        // because reliabilities are not plumbed to this layer.
        //
        // Ties keep the earliest mask, so the choice stays deterministic regardless of enumeration
        // order or worker count.
        Span<byte> best = stackalloc byte[CodewordLength];
        int bestWeight = int.MaxValue;
        for (int mask = 1; mask < 1 << count; mask++)
        {
            for (int i = 0; i < CodewordLength; i++)
                cwScratch[i] = buffer[i * cwCount + j];
            for (int b = 0; b < count; b++)
                if ((mask & (1 << b)) != 0)
                    cwScratch[positions[b]] = secondChoice[positions[b] * cwCount + j];
            if (reedSolomon.TryDecode(cwScratch, parity, out int weight) && weight < bestWeight)
            {
                bestWeight = weight;
                cwScratch.AsSpan(0, CodewordLength).CopyTo(best);
            }
        }
        if (bestWeight == int.MaxValue)
            return false;
        best.CopyTo(cwScratch);
        errors = SymbolsChanged(buffer, cwScratch, cwCount, j);
        return true;
    }

    /// <summary>
    /// Symbols the decode actually changed: the Hamming distance from the RECEIVED codeword to the
    /// decoded one. Chase must report this rather than the inner decoder's count, because the
    /// symbols it splices in from the second-choice stream are corrections — they are precisely the
    /// difference between what the classifier read and what was transmitted — and the inner decoder
    /// never sees them. It is handed the already-spliced word.
    ///
    /// In the all-flip branch that splice frequently makes the word syndrome-clean, so TryDecode
    /// returns true with correctedErrors == 0 and a codeword rescued from dozens of misread symbols
    /// reported none. Measured at parity 16, second choice holding the true value:
    ///
    ///     symbols wrong    3    6   10   25   60
    ///     reported         3    6   10    0    0
    ///
    /// The first three are the errors-only and erasure paths, which count correctly; the last two
    /// are Chase, past the point where the erasure retry can take the job.
    ///
    /// It is not a cosmetic miscount. CalibrationRunner divides CorrectedBytes by the parity budget
    /// to score a capture's ECC utilisation and recommends a DENSER setting when that comes back
    /// low — so a capture rescued at the very last resort scored 0% and was advised to push
    /// further. The quality heatmap read the same zero and drew every codeword clean.
    ///
    /// One 255-byte pass, on a path that has just run up to 63 Reed-Solomon decodes.
    /// </summary>
    private static int SymbolsChanged(byte[] buffer, byte[] cwScratch, int cwCount, int j)
    {
        int changed = 0;
        for (int i = 0; i < CodewordLength; i++)
            if (cwScratch[i] != buffer[i * cwCount + j])
                changed++;
        return changed;
    }

    private bool TryErasureRetry(byte[] buffer, int parity, int cwCount, int j, byte[] cwScratch,
        bool[] suspectBytes, out int errors)
    {
        errors = 0;

        // Collect this codeword's flagged symbols, up to the capacity the decoder will actually
        // honour — which is NOT the full erasure capacity. This comment used to say "f = parity
        // still decodes when every real error is marked", and that has been impossible since the
        // verification margin landed: the clamp four lines below caps f at parity − 2, and
        // ReedSolomon refuses 2·errors + erasures > nsym − 2 regardless. Two comments in one
        // method disagreeing about the same bound, with the correct one immediately below.
        Span<int> erasures = stackalloc int[MaxParity];
        int f = 0;
        // Matches the decoder's own ceiling: erasure correction that spends every syndrome leaves
        // nothing to verify with, so an over-flagged codeword is handed to Chase instead.
        //
        // Clamped, not just subtracted. Layout now rejects odd parity on decode as well as encode,
        // but this is the buffer's own bound and should not depend on a caller two layers away
        // getting that right: at parity 1 the raw expression is -1, which `f == limit` can never
        // match, and the loop then walks all 255 symbol positions into a 64-entry span.
        int limit = Math.Clamp(parity - ReedSolomon.VerificationMargin, 0, MaxParity);
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
