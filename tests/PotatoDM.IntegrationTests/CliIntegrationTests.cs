using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace PotatoDM.IntegrationTests;

public class CliIntegrationTests : IClassFixture<TestServerFixture>
{
	private sealed class CliProcess : IDisposable
	{
		private readonly Process _process;

		private readonly StringBuilder _stdout = new StringBuilder();

		private readonly StringBuilder _stderr = new StringBuilder();

		public int ExitCode => _process.ExitCode;

		public string Stdout
		{
			get
			{
				lock (_stdout)
				{
					return _stdout.ToString();
				}
			}
		}

		public string Stderr
		{
			get
			{
				lock (_stderr)
				{
					return _stderr.ToString();
				}
			}
		}

		public CliProcess(params string[] arguments)
		{
			string text = Path.Combine(AppContext.BaseDirectory, "cli", "PotatoDM.dll");
			Assert.True(File.Exists(text), "CLI was not copied to '" + text + "'.");
			ProcessStartInfo processStartInfo = new ProcessStartInfo(DotnetHost())
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			processStartInfo.ArgumentList.Add(text);
			foreach (string item in arguments)
			{
				processStartInfo.ArgumentList.Add(item);
			}
			_process = new Process
			{
				StartInfo = processStartInfo
			};
			_process.OutputDataReceived += delegate(object _, DataReceivedEventArgs eventArgs)
			{
				if (eventArgs.Data != null)
				{
					lock (_stdout)
					{
						_stdout.AppendLine(eventArgs.Data);
					}
				}
			};
			_process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs eventArgs)
			{
				if (eventArgs.Data != null)
				{
					lock (_stderr)
					{
						_stderr.AppendLine(eventArgs.Data);
					}
				}
			};
			_process.Start();
			_process.BeginOutputReadLine();
			_process.BeginErrorReadLine();
		}

		public async Task WaitForExitAsync()
		{
			await _process.WaitForExitAsync();
			_process.WaitForExit();
		}

		public void Kill()
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
			}
		}

		public void Dispose()
		{
			Kill();
			_process.Dispose();
		}

		private static string DotnetHost()
		{
			string? environmentVariable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			return (environmentVariable != null && environmentVariable.Length > 0) ? environmentVariable : "dotnet";
		}
	}

	private readonly TestServerFixture _server;

	public CliIntegrationTests(TestServerFixture server)
	{
		_server = server;
	}

	[Fact]
	public async Task Probe_command_emits_a_json_event()
	{
		string token = IntegrationHelpers.NewToken();
		string url = _server.Url("/dl?token=" + token + "&size=65536");
		using CliProcess process = new CliProcess("probe", url, "--json", "--no-proxy");
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {process.Stderr}");
		using JsonDocument document = JsonDocument.Parse(LastNonEmptyLine(process.Stdout));
		Assert.Equal("probe", document.RootElement.GetProperty("type").GetString());
		Assert.Equal(65536L, document.RootElement.GetProperty("totalLength").GetInt64());
		Assert.True(document.RootElement.GetProperty("supportsRanges").GetBoolean());
	}

	[Fact]
	public async Task Missing_url_returns_exit_code_two()
	{
		using CliProcess process = new CliProcess("download", "--json");
		await process.WaitForExitAsync();
		Assert.Equal(2, process.ExitCode);
		using JsonDocument document = JsonDocument.Parse(LastNonEmptyLine(process.Stdout));
		Assert.Equal("failed", document.RootElement.GetProperty("type").GetString());
		Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());
	}

    [Theory]
    [InlineData("--unknown", "unknown option '--unknown'")]
    [InlineData("--connections", "requires a value")]
    [InlineData("--quiet=true", "does not take a value")]
    public async Task Invalid_switches_report_argument_errors(string option, string message)
    {
        using var process = new CliProcess("download", option);
        await process.WaitForExitAsync();
        Assert.Equal(2, process.ExitCode);
        Assert.Contains(message, process.Stderr);
    }

    [Fact]
    public async Task Equals_options_and_end_of_options_preserve_the_download()
    {
        var token = IntegrationHelpers.NewToken();
        var url = _server.Url($"/dl?token={token}&size=4096");
        using var temp = new TempDirectory();
        var target = temp.FileFor("options.bin");
        using var process = new CliProcess(
            "download", "--output=" + target, "--connections=1", "--json", "--no-proxy", "--", url);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, process.Stderr);
        Assert.Equal(await _server.GetChecksumAsync(token, 4096), await IntegrationHelpers.Sha256Async(target));
    }

	[Fact]
	public async Task Max_speed_option_paces_the_download()
	{
		string token = IntegrationHelpers.NewToken();
		string url = _server.Url($"/dl?token={token}&size={1048576}");
		using TempDirectory temp = new TempDirectory();
		string target = temp.FileFor("limited-cli.bin");
		Stopwatch stopwatch = Stopwatch.StartNew();
		using CliProcess process = new CliProcess("download", url, "-o", target, "--connections", "1", "--max-speed", "524288", "--json", "--no-proxy");
		await process.WaitForExitAsync();
		stopwatch.Stop();
		Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {process.Stderr}");
		Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(1200.0), $"1 MiB at 512 KiB/s finished in {stopwatch.Elapsed}.");
		Assert.Equal(await _server.GetChecksumAsync(token, 1048576L), await IntegrationHelpers.Sha256Async(target));
	}

	[Fact]
	public async Task Dynamic_segments_option_splits_a_slow_range()
	{
		string token = IntegrationHelpers.NewToken();
		string url = _server.Url($"/dl?token={token}&size={8388608}&throttle=16777216" + "&slowStart=0&slowDelayMs=700");
		using TempDirectory temp = new TempDirectory();
		string target = temp.FileFor("dynamic-cli.bin");
		using CliProcess process = new CliProcess("download", url, "-o", target, "--connections", "2", "--min-segment-size", "524288", "--dynamic-segments", "--json", "--no-proxy");
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {process.Stderr}");
		Assert.Equal(await _server.GetChecksumAsync(token, 8388608L), await IntegrationHelpers.Sha256Async(target));
		List<long> starts = (from entry in (await _server.GetLogAsync(token)).Where(IntegrationHelpers.IsSegment)
			select IntegrationHelpers.ParseRangeHeader(entry.Range) into range
			where range.HasValue
			select range.Value.Start).ToList();
		Assert.True(starts.Count >= 3, "expected a stolen helper range, saw starts: " + string.Join(", ", starts));
		Assert.Contains(starts, (long start) => start > 0 && start != 4194304);
	}

	[Fact]
	public async Task Killed_process_resumes_from_a_partial_file()
	{
		string token = IntegrationHelpers.NewToken();
		string url = _server.Url($"/dl?token={token}&size={8388608}&throttle=1048576");
		using TempDirectory temp = new TempDirectory();
		string target = temp.FileFor("killed.bin");
		string statePath = target + ".download.json";
		using CliProcess download = new CliProcess("download", url, "-o", target, "--connections", "4", "--min-segment-size", "524288", "--max-retries", "3", "--json", "--no-proxy");
		Assert.True(await IntegrationHelpers.WaitUntilAsync(async delegate
		{
			bool flag = File.Exists(statePath);
			bool flag2 = flag;
			if (flag2)
			{
				flag2 = await IntegrationHelpers.BytesRecordedInStateAsync(statePath) > 0;
			}
			return flag2;
		}, TimeSpan.FromSeconds(20.0)), $"no progress was persisted before the timeout. stdout: {download.Stdout} stderr: {download.Stderr}");
		download.Kill();
		await download.WaitForExitAsync();
		Assert.True(File.Exists(statePath));
		Assert.False(File.Exists(target));
		List<RequestLogDto> beforeResume = await _server.GetLogAsync(token);
		int lastIndex = ((beforeResume.Count != 0) ? beforeResume.Max((RequestLogDto entry) => entry.Index) : 0);
		using CliProcess resume = new CliProcess("resume", statePath, "--json", "--no-proxy");
		await resume.WaitForExitAsync();
		Assert.True(resume.ExitCode == 0, $"resume exited {resume.ExitCode}. stdout: {resume.Stdout} stderr: {resume.Stderr}");
		using JsonDocument completed = JsonDocument.Parse(LastNonEmptyLine(resume.Stdout));
		Assert.Equal("completed", completed.RootElement.GetProperty("type").GetString());
		Assert.True(File.Exists(target));
		Assert.Equal(await _server.GetChecksumAsync(token, 8388608L), await IntegrationHelpers.Sha256Async(target));
		List<(long Start, long End)> resumedRanges = (from entry in await _server.GetLogAsync(token)
			where entry.Index > lastIndex && IntegrationHelpers.IsSegment(entry)
			select IntegrationHelpers.ParseRangeHeader(entry.Range) into range
			where range.HasValue
			select range.Value).ToList();
		Assert.NotEmpty(resumedRanges);
		Assert.Contains(resumedRanges, ((long Start, long End) range) => range.Start > 0);
	}

	private static string LastNonEmptyLine(string text)
	{
		return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
	}
}
