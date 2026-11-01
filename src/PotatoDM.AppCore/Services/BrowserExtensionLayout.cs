namespace PotatoDM.AppCore.Services;

/// <summary>
/// The files the browser extension cannot start without, and the check that refuses
/// to install when one of them is missing.
/// </summary>
/// <remarks>
/// <para>
/// Chromium loads the extension as a directory of classic scripts. A file that never
/// reaches the release payload therefore fails in the worst possible way: the install
/// reports success, the service worker dies on <c>importScripts()</c> before it
/// registers a single listener, and all the user sees is "the downloader does nothing".
/// There is no exception and no log line, because the process that would have written
/// it never started.
/// </para>
/// <para>
/// This list is the only thing standing between that failure and a visible
/// <see cref="FileNotFoundException"/> at install time, so it has to grow together with
/// <c>browser/shared/</c>. <c>BrowserExtensionLayoutTests</c> enforces that: it reads
/// the repository's own <c>browser/shared/</c> and <c>manifest.json</c> and fails when a
/// script or a manifest-referenced asset is missing from <see cref="RequiredSourceFiles"/>.
/// </para>
/// </remarks>
public static class BrowserExtensionLayout
{
    /// <summary>
    /// Installer paths, relative to the <c>browser-ext</c> directory shipped next to the
    /// executables.
    /// </summary>
    /// <remarks>
    /// The order matters only for the error message: the first missing file is the one
    /// the user is told about. <c>background.js</c> and its <c>importScripts()</c> targets
    /// come first because they are what turns an incomplete install into silence.
    /// </remarks>
    public static IReadOnlyList<string> RequiredSourceFiles { get; } = new[]
    {
        Path.Combine("shared", "background.js"),
        Path.Combine("shared", "settings.js"),
        Path.Combine("shared", "resources.js"),
        Path.Combine("shared", "protocol.js"),
        Path.Combine("shared", "content.js"),
        Path.Combine("shared", "content-main.js"),
        Path.Combine("shared", "popup.html"),
        Path.Combine("shared", "popup.js"),

        // Referenced by manifest.json ("icons" and "action.default_icon"). Chromium
        // rejects the whole extension when one of them is unreadable, so they belong
        // in the same pre-flight check as the scripts.
        Path.Combine("shared", "icons", "icon16.png"),
        Path.Combine("shared", "icons", "icon32.png"),
        Path.Combine("shared", "icons", "icon48.png"),
        Path.Combine("shared", "icons", "icon128.png"),

        Path.Combine("chromium", "manifest.json"),
    };

    /// <summary>
    /// Throws <see cref="FileNotFoundException"/> naming the first missing piece of the
    /// browser integration, so an incomplete build fails at install time instead of
    /// silently disabling every download hand-off.
    /// </summary>
    /// <param name="sourceRoot">The <c>browser-ext</c> directory next to the executables.</param>
    /// <param name="hostExePath">The native messaging host that Chromium will launch.</param>
    /// <exception cref="FileNotFoundException">A required file does not exist.</exception>
    public static void EnsureSources(string sourceRoot, string hostExePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceRoot);
        ArgumentException.ThrowIfNullOrEmpty(hostExePath);

        if (!File.Exists(hostExePath))
        {
            throw new FileNotFoundException("缺少浏览器宿主程序：" + hostExePath, hostExePath);
        }

        foreach (var relativePath in RequiredSourceFiles)
        {
            var path = Path.Combine(sourceRoot, relativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("缺少扩展文件：" + path, path);
            }
        }
    }
}
