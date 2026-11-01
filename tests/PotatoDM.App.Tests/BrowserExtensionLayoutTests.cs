using System.Text.Json;
using PotatoDM.AppCore.Services;

namespace PotatoDM.App.Tests;

/// <summary>
/// Guards the pre-flight check that runs before the browser integration is installed.
/// </summary>
/// <remarks>
/// The extension is a directory of classic scripts, so a file missing from the release
/// payload is not an exception anywhere: the MV3 service worker aborts on
/// <c>importScripts()</c> before registering its <c>downloads.onCreated</c> listener and
/// every hand-off disappears without a single error. <see cref="BrowserExtensionLayout"/>
/// is the only thing that can turn that into a visible failure, so both halves are
/// covered here: the check rejects an incomplete payload, and the checklist itself cannot
/// drift away from what the extension actually ships.
/// </remarks>
public class BrowserExtensionLayoutTests
{
    [Fact]
    public void A_missing_module_is_rejected_by_name()
    {
        using var temp = new TempDirectory();
        var (sourceRoot, hostExePath) = CreatePayload(temp, relative => relative != Path.Combine("shared", "settings.js"));

        var error = Assert.Throws<FileNotFoundException>(
            () => BrowserExtensionLayout.EnsureSources(sourceRoot, hostExePath));

        var expected = Path.Combine(sourceRoot, "shared", "settings.js");
        Assert.Equal(expected, error.FileName);
        Assert.Contains("settings.js", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings.js")]
    [InlineData("resources.js")]
    [InlineData("protocol.js")]
    public void Each_importScripts_target_is_checked(string fileName)
    {
        using var temp = new TempDirectory();
        var (sourceRoot, hostExePath) = CreatePayload(temp, relative => !relative.EndsWith(fileName, StringComparison.Ordinal));

        var error = Assert.Throws<FileNotFoundException>(
            () => BrowserExtensionLayout.EnsureSources(sourceRoot, hostExePath));

        Assert.Contains(fileName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_payload_passes()
    {
        using var temp = new TempDirectory();
        var (sourceRoot, hostExePath) = CreatePayload(temp, _ => true);

        BrowserExtensionLayout.EnsureSources(sourceRoot, hostExePath);
    }

    [Fact]
    public void A_missing_host_is_reported_before_the_extension_files()
    {
        using var temp = new TempDirectory();
        var (sourceRoot, _) = CreatePayload(temp, _ => true);
        // CreatePayload writes the real host, so point the check at a sibling that exists
        // only if the payload was never produced.
        var missingHost = Path.Combine(temp.Path, "not-built", "PotatoDM.BrowserHost.exe");

        var error = Assert.Throws<FileNotFoundException>(
            () => BrowserExtensionLayout.EnsureSources(sourceRoot, missingHost));

        Assert.Equal(missingHost, error.FileName);
    }

    [Fact]
    public void Every_script_the_extension_ships_is_on_the_checklist()
    {
        var shared = RepoRoot.Resolve("browser", "shared");
        var scripts = Directory.GetFiles(shared, "*.js", SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(scripts);

        foreach (var script in scripts)
        {
            var relative = Path.Combine("shared", Path.GetFileName(script));
            Assert.True(
                BrowserExtensionLayout.RequiredSourceFiles.Contains(relative),
                $"browser/shared/{Path.GetFileName(script)} is copied into the extension but is not checked by " +
                "BrowserExtensionLayout.RequiredSourceFiles. A missing script breaks the service worker silently, " +
                "so add it to the checklist.");
        }
    }

    [Fact]
    public void Every_manifest_reference_is_on_the_checklist()
    {
        var manifestPath = RepoRoot.Resolve("browser", "chromium", "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var element = manifest.RootElement;

        var references = new List<string>();

        if (element.TryGetProperty("background", out var background) &&
            background.TryGetProperty("service_worker", out var worker))
        {
            references.Add(worker.GetString()!);
        }

        if (element.TryGetProperty("action", out var action))
        {
            if (action.TryGetProperty("default_popup", out var popup))
            {
                references.Add(popup.GetString()!);
            }

            if (action.TryGetProperty("default_icon", out var defaultIcon))
            {
                AddIconValues(defaultIcon, references);
            }
        }

        if (element.TryGetProperty("icons", out var icons))
        {
            AddIconValues(icons, references);
        }

        if (element.TryGetProperty("content_scripts", out var contentScripts))
        {
            foreach (var contentScript in contentScripts.EnumerateArray())
            {
                if (contentScript.TryGetProperty("js", out var scripts))
                {
                    foreach (var script in scripts.EnumerateArray())
                    {
                        references.Add(script.GetString()!);
                    }
                }
            }
        }

        Assert.NotEmpty(references);

        foreach (var reference in references)
        {
            // Chromium rejects the whole extension when a manifest-referenced asset is
            // unreadable, which is the same silent-on-install failure as a missing script.
            var relative = Path.Combine("shared", reference.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                BrowserExtensionLayout.RequiredSourceFiles.Contains(relative),
                $"manifest.json references '{reference}' but BrowserExtensionLayout.RequiredSourceFiles does not " +
                "check it, so a payload missing that file would install \"successfully\" and then fail to load.");
        }
    }

    private static void AddIconValues(JsonElement icons, List<string> references)
    {
        foreach (var icon in icons.EnumerateObject())
        {
            if (icon.Value.ValueKind == JsonValueKind.String)
            {
                references.Add(icon.Value.GetString()!);
            }
        }
    }

    /// <summary>
    /// Builds a payload that satisfies every checklist entry <paramref name="include"/>
    /// accepts, and returns the <c>browser-ext</c> directory plus the host executable.
    /// </summary>
    private static (string SourceRoot, string HostExePath) CreatePayload(
        TempDirectory temp,
        Func<string, bool> include)
    {
        var sourceRoot = Path.Combine(temp.Path, "browser-ext");

        foreach (var relative in BrowserExtensionLayout.RequiredSourceFiles)
        {
            if (!include(relative))
            {
                continue;
            }

            var destination = Path.Combine(sourceRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, string.Empty);
        }

        var hostExePath = Path.Combine(temp.Path, "PotatoDM.BrowserHost.exe");
        File.WriteAllText(hostExePath, string.Empty);
        return (sourceRoot, hostExePath);
    }
}
