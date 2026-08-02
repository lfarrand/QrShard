using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace QrShard;

/// <summary>
/// Password-based payload encryption: AES-256-GCM with a PBKDF2-SHA256 key. The encrypted
/// blob is salt(16) | nonce(12) | tag(16) | ciphertext, so every parameter needed to decrypt
/// travels inside the shard payload itself; only the password stays out-of-band.
///
/// The GCM tag can also authenticate associated data (AAD) — the cleartext identity fields
/// around the ciphertext (original length, SHA-256, filename). Binding them means a tampered
/// filename/size/hash on a captured shard makes decryption fail up front instead of silently
/// mis-routing a write, closing the "GCM protects the payload but not the record around it" gap.
/// Old shards (no <see cref="ShardHeader.FlagAuthMeta"/>) decrypt with empty AAD, which GCM
/// treats identically to no AAD, so this is fully backward-compatible.
/// </summary>
internal sealed class PayloadCipher
{
    private readonly Action<byte[]>? keyClearedObserver;

    public PayloadCipher()
    {
    }

    /// <summary>Test seam invoked only after the derived-key array has been cleared.</summary>
    internal PayloadCipher(Action<byte[]> keyClearedObserver) => this.keyClearedObserver = keyClearedObserver;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 600_000; // OWASP-recommended for PBKDF2-SHA256; paid once per file

    public const int Overhead = SaltSize + NonceSize + TagSize;

    /// <summary>
    /// Allocates a blob for <see cref="SealInPlace"/>. The caller fills <see cref="Body"/> with the
    /// plaintext — reading it straight from its source, so the payload only ever exists once —
    /// and then seals it where it lies.
    /// </summary>
    public static byte[] AllocateBlob(long bodyLength)
    {
        long blobLength;
        try
        {
            blobLength = checked(bodyLength + Overhead);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("The encrypted payload is too large to materialize safely.");
        }
        if (bodyLength < 0 || blobLength > Array.MaxLength)
            throw new InvalidOperationException(
                "Password encryption requires one contiguous managed payload; split this input into smaller transfers.");
        return new byte[checked((int)blobLength)];
    }

    /// <summary>The payload region of a blob: plaintext before sealing, ciphertext after.</summary>
    public static Span<byte> Body(byte[] blob) => blob.AsSpan(Overhead);

    /// <summary>
    /// Encrypts a blob whose body already holds the plaintext, in place. GCM is CTR underneath, so
    /// each ciphertext byte replaces the plaintext byte at the same offset and no second full-size
    /// buffer is needed; the salt/nonce/tag prefix is a disjoint region. Exactly-coincident source
    /// and destination spans are the one overlap form <see cref="AesGcm"/> supports on every
    /// platform backend — the tests pin it, and CI runs them on the whole release matrix.
    /// </summary>
    public void SealInPlace(byte[] blob, string password, ReadOnlySpan<byte> aad = default)
    {
        bool sealedSuccessfully = false;
        try
        {
            Span<byte> salt = blob.AsSpan(0, SaltSize);
            Span<byte> nonce = blob.AsSpan(SaltSize, NonceSize);
            Span<byte> tag = blob.AsSpan(SaltSize + NonceSize, TagSize);
            Span<byte> body = blob.AsSpan(Overhead);

            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);
            byte[] key = DeriveKey(password, salt);
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, body, body, tag, aad);
                sealedSuccessfully = true;
            }
            finally
            {
                // AesGcm does not own or clear the caller-provided key array. Minimize how long the
                // PBKDF2 result remains in managed memory even on authentication/backend failures.
                ClearDerivedKey(key);
            }
        }
        finally
        {
            if (!sealedSuccessfully)
                CryptographicOperations.ZeroMemory(blob);
        }
    }

    public byte[] Encrypt(byte[] plaintext, string password, ReadOnlySpan<byte> aad = default)
    {
        byte[] blob = AllocateBlob(plaintext.Length);
        bool sealedSuccessfully = false;
        try
        {
            plaintext.CopyTo(Body(blob));
            SealInPlace(blob, password, aad);
            sealedSuccessfully = true;
            return blob;
        }
        finally
        {
            if (!sealedSuccessfully)
                CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="blob"/> in place and returns the plaintext as a slice of it, so a
    /// large decode never holds the payload twice. The authentication guarantee is unchanged: GCM
    /// still verifies the tag over the whole message before this returns, and on failure .NET
    /// zeroes the destination and throws — so the caller is handed either fully authenticated
    /// plaintext or an exception, never unverified bytes. The blob is already a private copy of
    /// the shard payloads, which stay intact for a retry with a different password.
    ///
    /// Throws <see cref="ShardDecodeException"/> on a wrong password, tampered data, or tampered
    /// associated data (the bound identity header).
    /// </summary>
    public ArraySegment<byte> DecryptInPlace(byte[] blob, string password, string fileName, ReadOnlySpan<byte> aad = default)
    {
        if (blob.Length < Overhead)
            throw new ShardDecodeException($"'{ShardHeader.Display(fileName)}': encrypted payload is truncated.");
        ReadOnlySpan<byte> salt = blob.AsSpan(0, SaltSize);
        ReadOnlySpan<byte> nonce = blob.AsSpan(SaltSize, NonceSize);
        ReadOnlySpan<byte> tag = blob.AsSpan(SaltSize + NonceSize, TagSize);
        Span<byte> body = blob.AsSpan(Overhead);

        byte[] key = DeriveKey(password, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            try
            {
                aes.Decrypt(nonce, body, tag, body, aad);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new ShardDecodeException($"'{ShardHeader.Display(fileName)}': wrong password, corrupted payload, or a tampered shard header.");
            }
        }
        finally
        {
            ClearDerivedKey(key);
        }
        return new ArraySegment<byte>(blob, Overhead, blob.Length - Overhead);
    }

    /// <summary>
    /// Canonical associated-data bytes binding the cleartext identity fields to the ciphertext:
    /// original length (8, little-endian) ‖ SHA-256 (32) ‖ filename (UTF-8). Reconstructed
    /// identically on encrypt (from the file) and decrypt (from the parsed header); any mismatch
    /// makes GCM authentication fail.
    /// </summary>
    public static byte[] BuildAad(long originalLength, ReadOnlySpan<byte> sha256, string fileName)
    {
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        var aad = new byte[8 + 32 + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(aad, originalLength);
        sha256[..32].CopyTo(aad.AsSpan(8));
        name.CopyTo(aad.AsSpan(40));
        return aad;
    }

    /// <summary>
    /// Current AAD suite. In addition to the v1 identity fields it domain-separates the protocol
    /// and binds every family-wide transformation flag. In particular an attacker cannot toggle
    /// archive extraction, compression, or FEC semantics while merely repairing the public header
    /// CRC. The parity bit is per-image and is therefore normalized out.
    /// </summary>
    public static byte[] BuildAadV2(long originalLength, ReadOnlySpan<byte> sha256, string fileName,
        byte flags)
    {
        ReadOnlySpan<byte> domain = "QrShard-AAD-v2:AES-256-GCM:PBKDF2-SHA256-600000\0"u8;
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        var aad = new byte[domain.Length + 1 + 8 + 32 + 4 + name.Length];
        int offset = 0;
        domain.CopyTo(aad);
        offset += domain.Length;
        aad[offset++] = (byte)(flags & ~ShardHeader.FlagParity);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(offset), originalLength);
        offset += 8;
        sha256[..32].CopyTo(aad.AsSpan(offset));
        offset += 32;
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset), name.Length);
        offset += 4;
        name.CopyTo(aad.AsSpan(offset));
        return aad;
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

    private void ClearDerivedKey(byte[] key)
    {
        CryptographicOperations.ZeroMemory(key);
        keyClearedObserver?.Invoke(key);
    }
}
