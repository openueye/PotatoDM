namespace PotatoDM.BrowserIpc.Tests;

public sealed class ChromeExtensionIdTests
{
    // The fixed development key from browser/chromium/manifest.json.
    private const string PublicKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAzQEMr1EOfNzBslURYcUx3fNj4eCenukJZZ5+FG8kGTADGhvZBaZwiLGqz0vPF6T/CytNlWhp8fqV1XlGQShyQhUEdAH2gTveXpgi9fUNTh17SK42VgC5fNN7CZaTjoUF4JF6hwm8PY3OZlN2T25XtI7YI/T1lkN68vkIrvtI26UpGf+FQ9Sq6F/VIRCOYG1Namb4lRm+RXlH841TqdWPaRgcJJ256OmCxyH2GkFbbaZQK3DxMs9dxo1kkChbDH0W+2t9og8QwvHRyFbLitFV2mBsdVJU7YQY+7hbQ6QfmnPwwizCSGThaGaTwu9SMRvwmT2gsc00OK0QNA3aaVbW2QIDAQAB";

    private const string ExpectedId = "andeadnkonaelnkcepalmkgidaggbnki";

    [Fact]
    public void FromPublicKey_DerivesChromeStyleNibbleEncodedId()
    {
        Assert.Equal(ExpectedId, ChromeExtensionId.FromPublicKey(PublicKey));
    }

    [Fact]
    public void FromPublicKey_IsStableAndTrimsWhitespace()
    {
        Assert.Equal(ExpectedId, ChromeExtensionId.FromPublicKey("  " + PublicKey + "\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromPublicKey_RejectsMissingKey(string key)
    {
        Assert.Throws<ArgumentException>(() => ChromeExtensionId.FromPublicKey(key));
    }

    [Fact]
    public void FromPublicKey_RejectsInvalidBase64()
    {
        Assert.Throws<ArgumentException>(
            () => ChromeExtensionId.FromPublicKey("not-a-key!"));
    }
}
