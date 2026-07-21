using BenchmarkDotNet.Running;
using QrShard.Benchmarks;

// `--graphs-only` regenerates the HTML graph report from previously persisted results
// (BenchmarkDotNet.Artifacts/results/transfer-results.json) without running any benchmarks.
if (args.Contains("--graphs-only"))
{
    GraphReport.WriteFromPersisted(
        Path.Combine(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts", "results"), Console.Out);
    return;
}

// `--gf-probe` runs the in-process GF(2^8) primitive micro-benchmark (throughput of the RS
// encode, syndrome scan, and MulAdd hot loops). Compare acceleration tiers by re-running with
// DOTNET_EnableGFNI=0 and/or DOTNET_EnableAVX512F=0 — same build, only the JIT path differs.
if (args.Contains("--gf-probe"))
{
    GfProbe.Run(Console.Out);
    return;
}

// Run everything by default (no interactive prompt); any BenchmarkDotNet switcher
// arguments (--filter, --list, --job, ...) pass straight through.
string[] effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;
var summaries = BenchmarkSwitcher.FromAssembly(typeof(TransferBenchmarks).Assembly)
    .Run(effectiveArgs)
    .ToList();

GraphReport.Write(summaries, Console.Out);
