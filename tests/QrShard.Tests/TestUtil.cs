using System.Security.Cryptography;

namespace QrShard.Tests;

/// <summary>Per-test temporary directory, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qrshard-tests-" + Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Sub(string name)
    {
        string dir = File(name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string WriteFile(string name, byte[] content)
    {
        string path = File(name);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best effort; leftover temp dirs are harmless.
        }
    }
}

public static class TestData
{
    public static byte[] Random(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static byte[] CompressibleText(int length)
    {
        var line = "The quick brown fox jumps over the lazy dog. 0123456789.\n"u8.ToArray();
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = line[i % line.Length];
        return data;
    }

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);
}
