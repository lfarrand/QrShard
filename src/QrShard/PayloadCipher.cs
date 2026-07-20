using System.Security.Cryptography;

namespace QrShard;

/// <summary>
/// Password-based payload encryption: AES-256-GCM with a PBKDF2-SHA256 key. The encrypted
/// blob is salt(16) | nonce(12) | tag(16) | ciphertext, so every parameter needed to decrypt
/// travels inside the shard payload itself; only the password stays out-of-band.
/// </summary>
internal sealed class PayloadCipher
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 600_000; // OWASP-recommended for PBKDF2-SHA256; paid once per file

    public const int Overhead = SaltSize + NonceSize + TagSize;

    public byte[] Encrypt(byte[] plaintext, string password)
    {
        var blob = new byte[Overhead + plaintext.Length];
        Span<byte> salt = blob.AsSpan(0, SaltSize);
        Span<byte> nonce = blob.AsSpan(SaltSize, NonceSize);
        Span<byte> tag = blob.AsSpan(SaltSize + NonceSize, TagSize);
        Span<byte> ciphertext = blob.AsSpan(Overhead);

        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(DeriveKey(password, salt), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return blob;
    }

    /// <summary>Throws <see cref="ShardDecodeException"/> on a wrong password or tampered data.</summary>
    public byte[] Decrypt(byte[] blob, string password, string fileName)
    {
        if (blob.Length < Overhead)
            throw new ShardDecodeException($"'{fileName}': encrypted payload is truncated.");
        ReadOnlySpan<byte> salt = blob.AsSpan(0, SaltSize);
        ReadOnlySpan<byte> nonce = blob.AsSpan(SaltSize, NonceSize);
        ReadOnlySpan<byte> tag = blob.AsSpan(SaltSize + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = blob.AsSpan(Overhead);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveKey(password, salt), TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new ShardDecodeException($"'{fileName}': wrong password (or the encrypted payload was corrupted).");
        }
        return plaintext;
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
}
