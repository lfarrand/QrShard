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

// `--readme-assets` re-exports the README's benchmark figures from those same persisted results:
// one standalone .svg per chart per theme into docs/benchmarks/, and the measurements table
// spliced into README.md between its BENCH:TABLE markers. Run it after a benchmark session.
if (args.Contains("--readme-assets"))
{
    string repoRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", ".."));
    GraphReport.WriteReadmeAssets(
        Path.Combine(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts", "results"),
        Path.Combine(repoRoot, "docs", "benchmarks"),
        Path.Combine(repoRoot, "README.md"),
        Console.Out);
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

// `--fps-probe` measures decode frame rate (single-core and parallel), payload bandwidth, and a
// 250 MB codec-bound transfer time across a selection of resolutions. In-process, ~30s total.
if (args.Contains("--fps-probe"))
{
    FpsProbe.Run(Console.Out);
    return;
}

// `--par-sweep` measures decode throughput against DecodeMaxParallelism (round-robin sampling,
// medians) to test the automatic worker cap. `--par-mem` reports the peak working set one worker
// count costs; run it once per count, in its own process, for an uncontaminated figure.
//
// `--par-compare` reports throughput, memory AND CPU for a single (workers, images) pair from one
// run, which is what you want when comparing two implementations rather than exploring a curve:
// stitching a figure together from separate runs describes no single configuration. Prefer it to
// --par-sweep for A/B work — par-sweep builds a decoder for every worker count up front and keeps
// them all alive, and that retained memory is not neutral between schemes. It measured a ~10%
// regression that way which one-pair-per-process showed did not exist.
//
//   for n in 50 100 120 200 240; do
//     dotnet run -c Release -- --par-compare --workers 24 --images $n
//   done
//
// Run one pair per process, and pair the two builds back-to-back per configuration so drift hits
// both sides of the comparison equally.
if (args.Contains("--par-sweep") || args.Contains("--par-mem") || args.Contains("--par-compare"))
{
    string Arg(string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
    string preset = Arg("--preset", "Max4K");
    // 96 images is the default because every default worker count divides it exactly; see
    // ParallelismSweep for why a ragged split invents a curve that is not there.
    if (args.Contains("--par-compare"))
        ParallelismSweep.Compare(Console.Out, preset, int.Parse(Arg("--workers", "24")),
            int.Parse(Arg("--images", "120")), int.Parse(Arg("--samples", "9")));
    else if (args.Contains("--par-mem"))
        ParallelismSweep.Memory(Console.Out, preset, int.Parse(Arg("--workers", "16")),
            int.Parse(Arg("--images", "96")));
    else
        ParallelismSweep.Run(Console.Out, preset, ParallelismSweep.ParseCounts(Arg("--workers", "8,12,16,24,32")),
            int.Parse(Arg("--samples", "15")), int.Parse(Arg("--images", "96")));
    return;
}

// Run everything by default (no interactive prompt); any BenchmarkDotNet switcher
// arguments (--filter, --list, --job, ...) pass straight through.
string[] effectiveArgs = args.Length == 0 ? ["--filter", "*"] : args;
var summaries = BenchmarkSwitcher.FromAssembly(typeof(TransferBenchmarks).Assembly)
    .Run(effectiveArgs)
    .ToList();

GraphReport.Write(summaries, Console.Out);
