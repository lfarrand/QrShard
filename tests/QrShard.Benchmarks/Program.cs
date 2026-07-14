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

// Run everything by default (no interactive prompt); any BenchmarkDotNet switcher
// arguments (--filter, --list, --job, ...) pass straight through.
string[] effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;
var summaries = BenchmarkSwitcher.FromAssembly(typeof(TransferBenchmarks).Assembly)
    .Run(effectiveArgs)
    .ToList();

GraphReport.Write(summaries, Console.Out);
