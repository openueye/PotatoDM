namespace PotatoDM.Tests;

/// <summary>
/// The directory that contains <c>PotatoDM.sln</c>, discovered by walking up from
/// the test binaries.
/// </summary>
/// <remarks>
/// A few tests assert on repository content itself — the README's exit-code table,
/// the list of scripts that ship inside the browser extension. None of those files
/// are copied next to the test assembly, and hardcoding an absolute path would break
/// the moment the checkout moves, so the root is looked up instead.
/// </remarks>
internal static class RepoRoot
{
    private static readonly Lazy<string> Cached = new(Find);

    /// <summary>Full path of the repository root.</summary>
    public static string Directory => Cached.Value;

    /// <summary>Combines <see cref="Directory"/> with <paramref name="segments"/>.</summary>
    public static string Resolve(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = Cached.Value;
        segments.CopyTo(parts, 1);

        return System.IO.Path.Combine(parts);
    }

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "PotatoDM.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "PotatoDM.sln was not found above " + AppContext.BaseDirectory +
            "; this test asserts on repository content and needs the checkout, not the installed layout.");
    }
}
