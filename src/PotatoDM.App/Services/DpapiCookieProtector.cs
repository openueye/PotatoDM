using System.Security.Cryptography;
using System.Text;
using PotatoDM.AppCore.Services;

namespace PotatoDM.App.Services;

/// <summary>Protects persisted browser cookies with the current Windows user's DPAPI key.</summary>
public sealed class DpapiCookieProtector : ICookieProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PotatoDM.Cookies.v1");

    public string? Protect(string plaintext)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return null;
        }

        try
        {
            var encrypted = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
