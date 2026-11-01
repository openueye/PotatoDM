using System.Security.Cryptography;
using System.Text;

namespace PotatoDM.BrowserIpc;

/// <summary>
/// Derives the stable Chrome extension id from the manifest "key" field:
/// SHA-256 of the DER public key, first 16 bytes, each nibble rendered as a-p.
/// </summary>
public static class ChromeExtensionId
{
    public static string FromPublicKey(string base64PublicKey)
    {
        if (string.IsNullOrWhiteSpace(base64PublicKey))
        {
            throw new ArgumentException("A base64 public key is required.", nameof(base64PublicKey));
        }

        byte[] der;
        try
        {
            der = Convert.FromBase64String(base64PublicKey.Trim());
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The public key is not valid base64.", nameof(base64PublicKey), ex);
        }

        var hash = SHA256.HashData(der);
        var builder = new StringBuilder(32);

        for (var index = 0; index < 16; index++)
        {
            builder.Append((char)('a' + (hash[index] >> 4)));
            builder.Append((char)('a' + (hash[index] & 0x0F)));
        }

        return builder.ToString();
    }
}
