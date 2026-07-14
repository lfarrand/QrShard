using System.Collections.Concurrent;

namespace QrShard;

/// <summary>
/// Reed-Solomon codec over GF(2^8) with polynomial 0x11D and first consecutive root α^0.
/// A codeword of n symbols with nsym parity symbols corrects up to nsym/2 unknown byte errors.
/// </summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];
    private static readonly ConcurrentDictionary<int, byte[]> Generators = new();

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] - Log[b] + 255];

    /// <summary>Generator polynomial Π(x + α^i) for i in 0..nsym-1, ascending-degree in reverse (index 0 = leading 1).</summary>
    private static byte[] Generator(int nsym) => Generators.GetOrAdd(nsym, n =>
    {
        var g = new byte[] { 1 };
        for (int i = 0; i < n; i++)
        {
            var ng = new byte[g.Length + 1];
            for (int j = 0; j < g.Length; j++)
            {
                ng[j] ^= g[j];                       // x * g[j]
                ng[j + 1] ^= Mul(g[j], Exp[i]);      // α^i * g[j]
            }
            g = ng;
        }
        return g;
    });

    /// <summary>Computes parity symbols for the data (systematic encoding, LFSR division).</summary>
    public static void Encode(ReadOnlySpan<byte> data, Span<byte> parity)
    {
        int nsym = parity.Length;
        if (nsym == 0)
            return;
        byte[] gen = Generator(nsym);
        parity.Clear();
        foreach (byte d in data)
        {
            byte coef = (byte)(d ^ parity[0]);
            for (int i = 0; i < nsym - 1; i++)
                parity[i] = parity[i + 1];
            parity[nsym - 1] = 0;
            if (coef != 0)
            {
                for (int i = 0; i < nsym; i++)
                    parity[i] ^= Mul(gen[i + 1], coef);
            }
        }
    }

    /// <summary>
    /// Corrects up to nsym/2 byte errors in place. The last nsym bytes of the codeword are parity.
    /// Returns false when the damage is uncorrectable (the codeword is left unspecified).
    /// </summary>
    public static bool TryDecode(Span<byte> codeword, int nsym, out int correctedErrors)
    {
        correctedErrors = 0;
        if (nsym == 0)
            return true;
        int n = codeword.Length;

        // Syndromes S_i = C(α^i); codeword[0] is the highest-degree coefficient.
        Span<byte> synd = stackalloc byte[nsym];
        bool clean = true;
        for (int i = 0; i < nsym; i++)
        {
            byte a = Exp[i], s = 0;
            foreach (byte c in codeword)
                s = (byte)(Mul(s, a) ^ c);
            synd[i] = s;
            clean &= s == 0;
        }
        if (clean)
            return true;

        // Berlekamp-Massey: error locator σ(x), ascending coefficients, σ[0] = 1.
        var sigma = new byte[] { 1 };
        var prev = new byte[] { 1 };
        int errors = 0, shift = 1;
        byte prevDelta = 1;
        for (int i = 0; i < nsym; i++)
        {
            byte delta = synd[i];
            for (int j = 1; j <= errors && j < sigma.Length; j++)
                delta ^= Mul(sigma[j], synd[i - j]);

            if (delta == 0)
            {
                shift++;
            }
            else if (2 * errors <= i)
            {
                byte[] t = sigma;
                sigma = AddScaledShifted(sigma, prev, Div(delta, prevDelta), shift);
                errors = i + 1 - errors;
                prev = t;
                prevDelta = delta;
                shift = 1;
            }
            else
            {
                sigma = AddScaledShifted(sigma, prev, Div(delta, prevDelta), shift);
                shift++;
            }
        }
        if (errors > nsym / 2)
            return false;

        // Chien search: error at index k when σ(X_k^-1) = 0, X_k = α^(n-1-k).
        Span<int> errPos = stackalloc int[errors];
        int found = 0;
        for (int k = 0; k < n; k++)
        {
            byte xInv = Exp[(255 - (n - 1 - k) % 255) % 255];
            if (EvalPoly(sigma, xInv) == 0)
            {
                if (found == errors)
                    return false; // more roots than the locator degree allows
                errPos[found++] = k;
            }
        }
        if (found != errors)
            return false;

        // Forney (fcr = 0): e_k = X_k * Ω(X_k^-1) / σ'(X_k^-1), Ω = S·σ mod x^nsym.
        Span<byte> omega = stackalloc byte[nsym];
        for (int i = 0; i < nsym; i++)
        {
            byte acc = 0;
            for (int j = 0; j <= i && j < sigma.Length; j++)
                acc ^= Mul(sigma[j], synd[i - j]);
            omega[i] = acc;
        }
        foreach (int k in errPos)
        {
            int logX = (n - 1 - k) % 255;
            byte xk = Exp[logX];
            byte xInv = Exp[(255 - logX) % 255];

            byte num = EvalPoly(omega, xInv);
            byte den = 0; // σ'(x): formal derivative keeps odd-degree terms only
            for (int j = 1; j < sigma.Length; j += 2)
                den ^= Mul(sigma[j], PowAt(xInv, j - 1));
            if (den == 0)
                return false;
            codeword[k] ^= Mul(xk, Div(num, den));
        }

        // Verify: all syndromes of the corrected codeword must vanish.
        for (int i = 0; i < nsym; i++)
        {
            byte a = Exp[i], s = 0;
            foreach (byte c in codeword)
                s = (byte)(Mul(s, a) ^ c);
            if (s != 0)
                return false;
        }
        correctedErrors = errors;
        return true;
    }

    /// <summary>σ + scale * x^shift * prev (ascending-coefficient polynomials).</summary>
    private static byte[] AddScaledShifted(byte[] sigma, byte[] prev, byte scale, int shift)
    {
        var result = new byte[Math.Max(sigma.Length, prev.Length + shift)];
        sigma.CopyTo(result, 0);
        for (int j = 0; j < prev.Length; j++)
            result[j + shift] ^= Mul(prev[j], scale);
        return result;
    }

    private static byte EvalPoly(ReadOnlySpan<byte> ascending, byte x)
    {
        byte result = 0;
        for (int j = ascending.Length - 1; j >= 0; j--)
            result = (byte)(Mul(result, x) ^ ascending[j]);
        return result;
    }

    private static byte PowAt(byte x, int power)
    {
        if (power == 0)
            return 1;
        if (x == 0)
            return 0;
        return Exp[Log[x] * power % 255];
    }
}
