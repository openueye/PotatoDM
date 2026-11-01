#nullable disable warnings

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using PotatoDM.AppCore.Services;
using PotatoDM.BrowserIpc;
using PotatoDM.Core.Storage;

namespace PotatoDM.App.Services;

public static class BrowserIntegrationInstaller
{
	public const string HostName = "com.potatodm.dm";

	private const string ChromeRegistryKey = "Software\\Google\\Chrome\\NativeMessagingHosts\\com.potatodm.dm";

	private const string EdgeRegistryKey = "Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.potatodm.dm";

	private const string LegacyFirefoxRegistryKey = "Software\\Mozilla\\NativeMessagingHosts\\com.potatodm.dm";

	private const string ChromeHostManifest = "com.potatodm.dm.chromium.json";

	private const string LegacyFirefoxHostManifest = "com.potatodm.dm.firefox.json";

	private static readonly JsonSerializerOptions ManifestJsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static string BrowserRoot => Path.Combine(AppPaths.Root, "browser");

	public static string ChromiumExtensionPath => Path.Combine(BrowserRoot, "chromium");

	private static string SourceRoot => Path.Combine(AppContext.BaseDirectory, "browser-ext");

	private static string HostExePath => Path.Combine(AppContext.BaseDirectory, "PotatoDM.BrowserHost.exe");

	private static string ChromiumManifestPath => Path.Combine(BrowserRoot, "com.potatodm.dm.chromium.json");

	private static string LegacyFirefoxExtensionPath => Path.Combine(BrowserRoot, "firefox");

	private static string LegacyFirefoxManifestPath => Path.Combine(BrowserRoot, "com.potatodm.dm.firefox.json");

	public static BrowserInstallStatus GetStatus()
	{
		string text = TryReadChromeId(Path.Combine(SourceRoot, "chromium", "manifest.json"));
		string text2 = TryReadChromeId(Path.Combine(ChromiumExtensionPath, "manifest.json"));
		return new BrowserInstallStatus
		{
			HostPresent = File.Exists(HostExePath),
			SourcePresent = Directory.Exists(SourceRoot),
			ChromeDetected = (DetectChrome() != null),
			EdgeDetected = (DetectEdge() != null),
			ChromeRegistered = IsRegistered("Software\\Google\\Chrome\\NativeMessagingHosts\\com.potatodm.dm", ChromiumManifestPath),
			EdgeRegistered = IsRegistered("Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.potatodm.dm", ChromiumManifestPath),
			ChromeId = (text2 ?? text),
			ChromePath = DetectChrome(),
			EdgePath = DetectEdge(),
			BrowserRoot = BrowserRoot,
			ChromiumExtensionPath = ChromiumExtensionPath,
			HostManifestPath = ChromiumManifestPath
		};
	}

	public static void Install()
	{
		EnsureSources();
		CleanupLegacyFirefox();
		Directory.CreateDirectory(ChromiumExtensionPath);
		CopyDirectory(Path.Combine(SourceRoot, "shared"), ChromiumExtensionPath);
		File.Copy(Path.Combine(SourceRoot, "chromium", "manifest.json"), Path.Combine(ChromiumExtensionPath, "manifest.json"), overwrite: true);
		string text = ReadChromeId(Path.Combine(ChromiumExtensionPath, "manifest.json"));
		WriteHostManifest(ChromiumManifestPath, new
		{
			name = "com.potatodm.dm",
			description = "PotatoDM browser download bridge",
			path = HostExePath,
			type = "stdio",
			allowed_origins = new string[1] { "chrome-extension://" + text + "/" }
		});
		Register("Software\\Google\\Chrome\\NativeMessagingHosts\\com.potatodm.dm", ChromiumManifestPath);
		Register("Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.potatodm.dm", ChromiumManifestPath);
	}

	public static void Uninstall()
	{
		DeleteRegistryKey("Software\\Google\\Chrome\\NativeMessagingHosts\\com.potatodm.dm");
		DeleteRegistryKey("Software\\Microsoft\\Edge\\NativeMessagingHosts\\com.potatodm.dm");
		FileSystemHelpers.TryDeleteFile(ChromiumManifestPath);
		TryDeleteDirectory(ChromiumExtensionPath);
		CleanupLegacyFirefox();
	}

	public static void OpenExtensionFolder()
	{
		Directory.CreateDirectory(BrowserRoot);
		Process.Start(new ProcessStartInfo("explorer.exe", "\"" + BrowserRoot + "\"")
		{
			UseShellExecute = true
		});
	}

	public static void OpenExtensionPage(BrowserKind browser)
	{
		(string, string) tuple = browser switch
		{
			BrowserKind.Chrome => (DetectChrome(), "chrome://extensions/"), 
			BrowserKind.Edge => (DetectEdge(), "edge://extensions/"), 
			_ => (null, string.Empty), 
		};
		var (text, arguments) = tuple;
		if (string.IsNullOrEmpty(text))
		{
			throw new InvalidOperationException("没有检测到该浏览器。");
		}
		Process.Start(new ProcessStartInfo(text)
		{
			Arguments = arguments,
			UseShellExecute = true
		});
	}

	public static string? DetectChrome()
	{
		return DetectBrowser("chrome.exe", FirstExisting(Path.Combine(ProgramFiles(), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(ProgramFilesX86(), "Google", "Chrome", "Application", "chrome.exe"), Path.Combine(LocalAppData(), "Google", "Chrome", "Application", "chrome.exe")));
	}

	public static string? DetectEdge()
	{
		return DetectBrowser("msedge.exe", FirstExisting(Path.Combine(ProgramFilesX86(), "Microsoft", "Edge", "Application", "msedge.exe"), Path.Combine(ProgramFiles(), "Microsoft", "Edge", "Application", "msedge.exe")));
	}

	private static void EnsureSources()
	{
		// The file list lives in AppCore so that a net8.0 test can assert an incomplete
		// payload is rejected; this WPF project is not reachable from the test projects.
		BrowserExtensionLayout.EnsureSources(SourceRoot, HostExePath);
	}

	private static string ReadChromeId(string manifestPath)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
		if (!jsonDocument.RootElement.TryGetProperty("key", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
		{
			throw new InvalidOperationException("Chromium 扩展 manifest 缺少 key。");
		}
		return ChromeExtensionId.FromPublicKey(value.GetString());
	}

	private static string? TryReadChromeId(string manifestPath)
	{
		try
		{
			return File.Exists(manifestPath) ? ReadChromeId(manifestPath) : null;
		}
		catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
		{
			return null;
		}
	}

	private static void CleanupLegacyFirefox()
	{
		DeleteRegistryKey("Software\\Mozilla\\NativeMessagingHosts\\com.potatodm.dm");
		FileSystemHelpers.TryDeleteFile(LegacyFirefoxManifestPath);
		TryDeleteDirectory(LegacyFirefoxExtensionPath);
	}

	private static void WriteHostManifest(string path, object content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllText(path, JsonSerializer.Serialize(content, ManifestJsonOptions));
	}

	private static void Register(string keyPath, string manifestPath)
	{
		using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(keyPath, writable: true) ?? throw new InvalidOperationException("无法写入注册表：" + keyPath);
		registryKey.SetValue(null, manifestPath, RegistryValueKind.String);
	}

	private static void DeleteRegistryKey(string keyPath)
	{
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static bool IsRegistered(string keyPath, string expectedManifestPath)
	{
		string text = Registry.GetValue("HKEY_CURRENT_USER\\" + keyPath, string.Empty, null) as string;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		try
		{
			return string.Equals(Path.GetFullPath(text), Path.GetFullPath(expectedManifestPath), StringComparison.OrdinalIgnoreCase) && File.Exists(text);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (string item in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			string relativePath = Path.GetRelativePath(source, item);
			string text = Path.Combine(destination, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(text));
			File.Copy(item, text, overwrite: true);
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string? DetectBrowser(string executableName, string? fallback)
	{
		string[] array = new string[2] { "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" };
		foreach (string text in array)
		{
			try
			{
				if (Registry.GetValue(text + executableName, string.Empty, null) is string text2 && !string.IsNullOrWhiteSpace(text2) && File.Exists(text2))
				{
					return text2;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
			{
			}
		}
		return fallback;
	}

	private static string? FirstExisting(params string?[] candidates)
	{
		return candidates.FirstOrDefault((string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
	}

	private static string ProgramFiles()
	{
		return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
	}

	private static string ProgramFilesX86()
	{
		return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
	}

	private static string LocalAppData()
	{
		return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
	}
}
