using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PotatoDM.Core.Media;

/// <summary>
/// AES-128-CBC decryption for media segments (RFC 8216 section 5.2). Decryption
/// only; this class never encrypts or bypasses content protection.
/// </summary>
public static class Aes128Cbc
{
    private const int KeySize = 16;
    private const int IvSize = 16;

    /// <summary>
    /// Decrypts <paramref name="ciphertext"/> with AES-128-CBC/PKCS#7 and returns
    /// the plaintext with the padding removed.
    /// </summary>
    /// <param name="ciphertext">The ciphertext, including its PKCS#7 padding.</param>
    /// <param name="key">The 16-byte AES key.</param>
    /// <param name="iv">The 16-byte initialization vector.</param>
    /// <returns>The decrypted plaintext; empty when <paramref name="ciphertext"/> is empty.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> or <paramref name="iv"/> is not exactly 16 bytes.
    /// </exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, byte[] key, byte[] iv)
    {
        ValidateKeyAndIv(key, iv);

        if (ciphertext.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        using var aes = CreateAes(key, iv);
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
    }

    /// <summary>
    /// Wraps <paramref name="ciphertext"/> in a read-only AES-128-CBC/PKCS#7
    /// decryption stream. Disposing the returned <see cref="CryptoStream"/> also
    /// disposes <paramref name="ciphertext"/>, so the caller must not use it afterwards.
    /// </summary>
    /// <param name="ciphertext">The stream of encrypted bytes; owned by the returned stream.</param>
    /// <param name="key">The 16-byte AES key.</param>
    /// <param name="iv">The 16-byte initialization vector.</param>
    /// <returns>A readable stream that yields the decrypted plaintext.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> or <paramref name="iv"/> is not exactly 16 bytes.
    /// </exception>
    public static Stream CreateDecryptingStream(Stream ciphertext, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ValidateKeyAndIv(key, iv);

        var aes = CreateAes(key, iv);
        ICryptoTransform? decryptor = null;
        try
        {
            decryptor = aes.CreateDecryptor();
            return new CryptoStream(ciphertext, decryptor, CryptoStreamMode.Read, leaveOpen: false);
        }
        catch
        {
            decryptor?.Dispose();
            aes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Derives the 16-byte AES-CBC IV for a segment: eight zero bytes followed by
    /// the sequence number as a big-endian unsigned 64-bit value (RFC 8216 section 5.2).
    /// </summary>
    /// <param name="sequenceNumber">The media sequence number of the segment.</param>
    /// <returns>The 16-byte initialization vector.</returns>
    public static byte[] DeriveIv(long sequenceNumber)
    {
        var iv = new byte[IvSize];
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(IvSize / 2), unchecked((ulong)sequenceNumber));
        return iv;
    }

    private static Aes CreateAes(byte[] key, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static void ValidateKeyAndIv(byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);

        if (key.Length != KeySize)
        {
            throw new ArgumentException($"AES-128 key must be exactly {KeySize} bytes.", nameof(key));
        }

        if (iv.Length != IvSize)
        {
            throw new ArgumentException($"AES-CBC IV must be exactly {IvSize} bytes.", nameof(iv));
        }
    }
}
