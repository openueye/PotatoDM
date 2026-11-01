using System.Security.Cryptography;
using System.Text;
using PotatoDM.Core.Media;

namespace PotatoDM.Core.Tests;

public class Aes128CbcTests
{
    private static readonly byte[] Key =
    {
        0x2b, 0x7e, 0x15, 0x16, 0x28, 0xae, 0xd2, 0xa6,
        0xab, 0xf7, 0x15, 0x88, 0x09, 0xcf, 0x4f, 0x3c,
    };

    private static readonly byte[] Iv =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
    };

    [Fact]
    public void Decrypt_round_trips_known_plaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.");

        var ciphertext = Encrypt(plaintext, Key, Iv);

        Assert.Equal(plaintext, Aes128Cbc.Decrypt(ciphertext, Key, Iv));
    }

    [Fact]
    public void Decrypt_rejects_a_key_that_is_not_16_bytes()
    {
        Assert.Throws<ArgumentException>(
            () => Aes128Cbc.Decrypt(new byte[] { 1, 2, 3 }, new byte[15], Iv));
    }

    [Fact]
    public void Decrypt_rejects_an_iv_that_is_not_16_bytes()
    {
        Assert.Throws<ArgumentException>(
            () => Aes128Cbc.Decrypt(new byte[] { 1, 2, 3 }, Key, new byte[17]));
    }

    [Fact]
    public void Decrypt_returns_empty_for_empty_ciphertext()
    {
        Assert.Empty(Aes128Cbc.Decrypt(ReadOnlySpan<byte>.Empty, Key, Iv));
    }

    [Fact]
    public void DeriveIv_of_zero_is_all_zero_bytes()
    {
        Assert.Equal(new byte[16], Aes128Cbc.DeriveIv(0));
    }

    [Fact]
    public void DeriveIv_writes_the_sequence_number_big_endian()
    {
        var expected = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        };

        Assert.Equal(expected, Aes128Cbc.DeriveIv(0x0102030405060708));
    }

    [Fact]
    public void CreateDecryptingStream_matches_Decrypt_on_a_multi_block_payload()
    {
        var plaintext = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        var ciphertext = Encrypt(plaintext, Key, Iv);

        using var decrypting = Aes128Cbc.CreateDecryptingStream(new MemoryStream(ciphertext), Key, Iv);
        using var output = new MemoryStream();
        decrypting.CopyTo(output);

        Assert.Equal(plaintext, output.ToArray());
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }
}
