namespace PotatoDM.AppCore.Services;

/// <summary>
/// Protects request cookies before they are written to the job registry.
/// The WPF app uses DPAPI; tests can use an in-memory stand-in.
/// </summary>
public interface ICookieProtector
{
    string? Protect(string plaintext);

    string? Unprotect(string? protectedText);
}
