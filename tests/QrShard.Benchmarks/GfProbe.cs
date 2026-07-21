using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using QrShard;

namespace QrShard.Benchmarks;

/// <summary>
/// In-process micro-benchmark of the GF(2^8) hot loops — the level where SIMD-tier changes are
/// visible (CLI wall-clock is dominated by process startup, PNG I/O, and memory bandwidth).
/// Run with DOTNET_EnableGFNI=0 / DOTNET_EnableAVX512F=0 to measure the fallback tiers of the
/// same binary.
/// </summary>
internal static class GfProbe
{
    public static void Run(TextWriter output)
    {
        string tier = Gfni.V512.IsSupported ? "GFNI-V512"
            : Gfni.V256.IsSupported ? "GFNI-V256"
            : Gfni.IsSupported ? "GFNI-V128"
            : System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated ? "shuffle-V128"
            : "scalar";
        output.WriteLine($"acceleration tier: {tier}");

        const int parity = 16, cwCount = 1024;
        int dataLen = Fec.DataLength(parity);
        var fec = new Fec();
        var gf = new Gf256();
        var rng = new Random(42);
        var stream = new byte[cwCount * dataLen];
        rng.NextBytes(stream);
        var buffer = new byte[cwCount * Fec.CodewordLength];
        var dest = new byte[stream.Length];
        var mulSrc = new byte[1 << 20];
        var mulDst = new byte[1 << 20];
        rng.NextBytes(mulSrc);

        Measure(output, "RS encode (ProtectInto)     ", stream.Length,
            () => fec.ProtectInto(stream, stream.Length, parity, cwCount, buffer));
        Measure(output, "RS syndrome scan (clean)    ", stream.Length,
            () => fec.TryRecoverInto(buffer, parity, cwCount, dest, out _));
        Measure(output, "GF MulAdd (Cauchy/fountain) ", mulSrc.Length,
            () => gf.MulAdd(0xA7, mulSrc, mulDst));
    }

    private static void Measure(TextWriter output, string name, long bytesPerOp, Action op)
    {
        op(); // warm up + JIT
        op();
        var sw = Stopwatch.StartNew();
        int reps = 0;
        while (sw.ElapsedMilliseconds < 1500)
        {
            op();
            reps++;
        }
        sw.Stop();
        double gbPerSec = bytesPerOp * (double)reps / sw.Elapsed.TotalSeconds / 1e9;
        output.WriteLine($"{name}: {gbPerSec,8:0.00} GB/s  ({reps} reps)");
    }
}
