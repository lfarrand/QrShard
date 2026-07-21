using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Randomized robustness harness over every parser that consumes untrusted bytes: decode takes
/// arbitrary captured images from unknown tools, sessions and clipboards arbitrary files and
/// bitmaps. Deterministically seeded (failures print the seed for replay); iteration depth
/// scales via QRSHARD_FUZZ_ITERATIONS — small on PR CI, deep on the scheduled fuzz workflow.
/// The invariant everywhere: garbage may be rejected (null/false/ShardDecodeException), it may
/// NEVER throw anything else or crash.
/// </summary>
public class FuzzTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("QRSHARD_FUZZ_ITERATIONS"), out int n) && n > 0 ? n : 300;

    private static byte[] Mutate(byte[] source, Random rng)
    {
        int length = rng.Next(4) switch
        {
            0 => rng.Next(source.Length + 1),                  // truncate
            1 => source.Length + rng.Next(1, 64),              // extend with junk
            _ => source.Length,
        };
        var data = new byte[length];
        Array.Copy(source, data, Math.Min(source.Length, length));
        for (int i = source.Length; i < length; i++)
            data[i] = (byte)rng.Next(256);
        int mutations = rng.Next(1, 24);
        for (int i = 0; i < mutations && data.Length > 0; i++)
        {
            int pos = rng.Next(data.Length);
            data[pos] = rng.Next(2) == 0 ? (byte)(data[pos] ^ (1 << rng.Next(8))) : (byte)rng.Next(256);
        }
        return data;
    }

    private static void Run(string target, Action<Random, int> iteration)
    {
        int iterations = Iterations;
        for (int seed = 0; seed < iterations; seed++)
        {
            try
            {
                iteration(new Random(seed), seed);
            }
            catch (ShardDecodeException)
            {
                // the domain's rejection — always acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"{target} crashed at seed {seed}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void FastPngReader_NeverCrashes_OnGarbage()
    {
        using var tmp = new TempDir();
        // Seed corpus: a real shard PNG to mutate, plus pure noise.
        string input = tmp.WriteFile("input.bin", TestData.Random(5_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });
        byte[] valid = File.ReadAllBytes(result.Files[0]);
        var reader = new FastPngReader();
        var scratch = new DecodeScratch();

        Run("FastPngReader", (rng, seed) =>
        {
            byte[] data = rng.Next(3) == 0 ? Mutate(TestData.Random(rng.Next(2048), seed), rng) : Mutate(valid, rng);
            string path = tmp.File($"fuzz.png");
            File.WriteAllBytes(path, data);
            reader.TryRead(path, scratch, out _); // false or true — never a crash
        });
    }

    [Fact]
    public void CraftedValidHeaders_ThroughReassemblyMath_NeverCrash()
    {
        // Byte-mutation rarely lands on a valid header CRC, so it never exercises the stripe
        // MATH with crafted-but-accepted geometry. This target builds CRC-valid headers with
        // random field values, then runs deserialize + the completeness check + assembly —
        // the divisor/array-size paths — asserting only ShardDecodeException may escape.
        var crc = new Crc();
        var parity = new ParityReassembler();
        var assembler = new ShardAssembler();
        for (int seed = 0; seed < Iterations; seed++)
        {
            var rng = new Random(seed);
            int count = rng.Next(1, 40);
            int stripeData = rng.Next(0, 40);
            int stripeParity = rng.Next(0, 40);
            byte flags = (byte)(rng.Next(2) == 0 ? 0 : ShardHeader.FlagParity | (rng.Next(2) == 0 ? ShardHeader.FlagFountain : 0));
            var payload = TestData.Random(rng.Next(1, 32), seed);
            var header = new ShardHeader
            {
                FileId = (ulong)rng.Next(),
                Index = rng.Next(0, 40),
                Count = count,
                PayloadLength = payload.Length,
                PayloadCrc32 = crc.Crc32(payload),
                TotalLength = rng.Next(0, 10_000),
                OriginalLength = rng.Next(0, 10_000),
                Flags = flags,
                Sha256 = new byte[32],
                FileName = "f.bin",
                StripeData = stripeData,
                StripeParity = stripeParity,
            };

            // A directly-constructed shard bypasses Deserialize's guards, so completeness and
            // assembly must be total on their own.
            var shard = new DecodedShard(header, payload, "crafted", 0, 0);
            try
            {
                parity.IsSetComplete([shard]);
                assembler.Assemble([shard], null, _ => { });
            }
            catch (ShardDecodeException)
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"crafted header crashed at seed {seed}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void ShardHeader_Deserialize_NeverCrashes()
    {
        var header = new ShardHeader
        {
            FileId = 1,
            Index = 0,
            Count = 1,
            PayloadLength = 10,
            PayloadCrc32 = 0,
            TotalLength = 10,
            OriginalLength = 10,
            Flags = 0,
            Sha256 = new byte[32],
            FileName = "f.bin",
        };
        byte[] valid = header.Serialize();

        Run("ShardHeader.Deserialize", (rng, _) =>
        {
            byte[] data = rng.Next(2) == 0 ? Mutate(valid, rng) : Mutate(new byte[rng.Next(256)], rng);
            ShardHeader.Deserialize(data, out _);
        });
    }

    [Fact]
    public void MetadataStrip_Unpack_NeverCrashes()
    {
        Run("Layout.UnpackMetadata", (rng, _) =>
        {
            var modules = new bool[Layout.MetaModuleCount];
            for (int i = 0; i < modules.Length; i++)
                modules[i] = rng.Next(2) == 0;
            Layout.UnpackMetadata(modules);
        });
    }

    [Fact]
    public void SessionStore_Load_NeverCrashes()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(20_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });
        var shards = new ShardDecoder().CollectShards(result.Files, _ => { });
        var store = new SessionStore();
        string validSession = tmp.File("valid.qrsession");
        store.Save(validSession, shards);
        byte[] valid = File.ReadAllBytes(validSession);

        Run("SessionStore.Load", (rng, _) =>
        {
            byte[] data = rng.Next(2) == 0 ? Mutate(valid, rng) : Mutate(new byte[rng.Next(512)], rng);
            string path = tmp.File("fuzz.qrsession");
            File.WriteAllBytes(path, data);
            store.Load(path);
        });
    }

    [Fact]
    public void PayloadCipher_Decrypt_NeverCrashes()
    {
        var cipher = new PayloadCipher();
        byte[] valid = cipher.Encrypt(TestData.Random(200), "pw");

        Run("PayloadCipher.Decrypt", (rng, _) =>
        {
            byte[] data = rng.Next(2) == 0 ? Mutate(valid, rng) : new byte[rng.Next(200)];
            cipher.Decrypt(data, "pw", "fuzz");
        });
    }

    [Fact]
    public void ClipboardDib_Parse_NeverCrashes()
    {
        Run("ClipboardReader.ParseDib", (rng, seed) =>
            ClipboardReader.ParseDib(Mutate(TestData.Random(rng.Next(4096), seed), rng)));
    }

    [Fact]
    public void DecodeImage_OnNoiseImages_OnlyRejectsCleanly()
    {
        using var tmp = new TempDir();
        var decoder = new ShardDecoder();
        var scratch = new DecodeScratch();
        var png = new FastPng();
        var rng = new Random(1234);

        int iterations = Math.Max(10, Iterations / 10); // image-sized inputs — keep it bounded
        for (int i = 0; i < iterations; i++)
        {
            int w = rng.Next(16, 300), h = rng.Next(16, 300);
            var px = new SixLabors.ImageSharp.PixelFormats.Rgb24[w * h];
            var bytes = new byte[px.Length * 3];
            rng.NextBytes(bytes);
            for (int p = 0; p < px.Length; p++)
                px[p] = new SixLabors.ImageSharp.PixelFormats.Rgb24(bytes[p * 3], bytes[p * 3 + 1], bytes[p * 3 + 2]);
            string path = tmp.File("noise.png");
            png.Write(path, px, w, h, upFilter: false, System.IO.Compression.CompressionLevel.Fastest);
            try
            {
                decoder.DecodeImage(path, scratch);
                Assert.Fail("noise decoded to a valid shard — CRC/ECC gates are broken");
            }
            catch (ShardDecodeException)
            {
                // the only acceptable outcome
            }
        }
    }
}
