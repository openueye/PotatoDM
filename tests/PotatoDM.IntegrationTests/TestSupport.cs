using PotatoDM.AppCore.Services;

namespace PotatoDM.IntegrationTests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

internal sealed class PassthroughCookieProtector : ICookieProtector
{
    public string? Protect(string plaintext) => "protected:" + plaintext;

    public string? Unprotect(string? protectedText) =>
        protectedText is not null && protectedText.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedText["protected:".Length..]
            : null;
}
