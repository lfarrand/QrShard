using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace QrShard.Benchmarks;

/// <summary>
/// Macro-benchmarks for the full transfer pipeline: file → shard PNGs on disk (Encode) and
/// shard PNGs → verified byte-identical file (Decode).
///
/// These are long-running, IO-heavy operations, so the Monitoring run strategy is used
/// (1 launch, 1 warmup, 3 measured iterations per case) — the standard BenchmarkDotNet
/// approach for macro-benchmarks where the default micro-benchmark rigor would take days.
///
/// The decoded output is SHA-256-verified against the generated input every iteration
/// (in IterationCleanup, so verification cost never pollutes the Decode timing; the decoder
/// additionally self-verifies against the SHA carried inside the shards).
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
[RPlotExporter]
public class TransferBenchmarks
{
    public static IEnumerable<string> FileSizes =>
        (Environment.GetEnvironmentVariable("QRSHARD_BENCH_SIZES") ?? BenchPresets.DefaultSizes)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static IEnumerable<string> Presets =>
        (Environment.GetEnvironmentVariable("QRSHARD_BENCH_PRESETS") ?? BenchPresets.DefaultPresets)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [ParamsSource(nameof(FileSizes))]
    public string FileSize { get; set; } = "1KB";

    [ParamsSource(nameof(Presets))]
    public string Preset { get; set; } = "Default";

    private string _root = "";
    private string _inputPath = "";
    private string _decodeShardDir = "";
    private string _encodeOutDir = "";
    private string _decodeOutPath = "";
    private byte[] _inputSha = [];
    private EncodeOptions _options = new();

    // ---------- Setup ----------

    private void CommonSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"qrshard-bench-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(_root);
        _options = BenchPresets.Options(Preset);
        _inputPath = Path.Combine(_root, BenchPresets.PayloadName);

        // Deterministic, incompressible content (a zip-like payload), generated in 8 MB
        // chunks so the 1 GB case does not need a second gigabyte-sized buffer.
        long size = BenchPresets.ParseSize(FileSize);
        var rng = new Random(unchecked((int)(size % int.MaxValue) * 31 + Preset.Length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var fs = File.Create(_inputPath))
        {
            var buffer = new byte[8 * 1024 * 1024];
            long remaining = size;
            while (remaining > 0)
            {
                int n = (int)Math.Min(buffer.Length, remaining);
                rng.NextBytes(buffer.AsSpan(0, n));
                fs.Write(buffer, 0, n);
                hash.AppendData(buffer, 0, n);
                remaining -= n;
            }
        }
        _inputSha = hash.GetHashAndReset();
    }

    [GlobalSetup(Target = nameof(Encode))]
    public void SetupEncode() => CommonSetup();

    [GlobalSetup(Target = nameof(Decode))]
    public void SetupDecode()
    {
        CommonSetup();
        _decodeShardDir = Path.Combine(_root, "shards");
        new ShardEncoder().Encode(_inputPath, _decodeShardDir, _options);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort — temp dirs
        }
    }

    // ---------- Encode ----------

    [IterationSetup(Target = nameof(Encode))]
    public void IterationSetupEncode() =>
        _encodeOutDir = Path.Combine(_root, $"enc-{Guid.NewGuid().ToString("N")[..8]}");

    [Benchmark]
    public int Encode() => new ShardEncoder().Encode(_inputPath, _encodeOutDir, _options).ImageCount;

    [IterationCleanup(Target = nameof(Encode))]
    public void IterationCleanupEncode()
    {
        if (Directory.Exists(_encodeOutDir))
            Directory.Delete(_encodeOutDir, recursive: true);
    }

    // ---------- Decode ----------

    [IterationSetup(Target = nameof(Decode))]
    public void IterationSetupDecode() =>
        _decodeOutPath = Path.Combine(_root, $"out-{Guid.NewGuid().ToString("N")[..8]}.bin");

    [Benchmark]
    public long Decode()
    {
        var restored = new ShardDecoder().DecodeFolder(
            Directory.EnumerateFiles(_decodeShardDir, "*.png"), _decodeOutPath, _ => { });
        return restored[0].Length;
    }

    [IterationCleanup(Target = nameof(Decode))]
    public void IterationCleanupDecode()
    {
        // Received file must match the sent file — verify outside the timed region.
        using (var fs = File.OpenRead(_decodeOutPath))
        {
            byte[] sha = SHA256.HashData(fs);
            if (!sha.AsSpan().SequenceEqual(_inputSha))
                throw new InvalidOperationException(
                    $"BENCHMARK INTEGRITY FAILURE: decoded output does not match input ({FileSize}, {Preset}).");
        }
        File.Delete(_decodeOutPath);
    }
}
