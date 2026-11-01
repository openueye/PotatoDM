using System.Globalization;
using System.Net;
using System.Reflection;
using PotatoDM.Core.Exceptions;
using PotatoDM.Core.Http;
using PotatoDM.Core.Models;
using PotatoDM.Core.Transfer;

namespace PotatoDM.Core.Tests;

/// <summary>
/// Pins the two places the CLI's exit codes are written down: the constants and the
/// README table.
/// </summary>
/// <remarks>
/// The reason this exists: <c>HttpDownloadException</c> used to compute
/// <c>isTransient</c>, pass it around, and then hardcode exit 4, so every 503 told a
/// caller "the URL is wrong, do not retry". A flag that is carried and then dropped is
/// invisible to a compiler and to any test that only looks at the flag, so the mapping
/// and the documented table are asserted directly.
/// </remarks>
public class ExitCodeTests
{
    private static readonly Uri ProbeUrl = new("http://127.0.0.1:5099/dl?token=test");

    // maxRetries: 0 — the probe would otherwise retry the transient statuses this test
    // asserts on, and the test would be about the retry policy instead of the exit code.
    private static readonly UrlProbe Probe = new(new RetryPolicy(maxRetries: 0));

    [Fact]
    public void A_transient_http_failure_reports_the_retryable_code()
    {
        var error = new HttpDownloadException("HTTP 503 (ServiceUnavailable).", isTransient: true);

        Assert.True(error.IsTransient);
        Assert.Equal(DownloadExitCode.TransientFailure, error.ExitCode);
        // The literal is deliberate: renaming the constant must not silently move the
        // number a shell script already depends on.
        Assert.Equal(9, error.ExitCode);
    }

    [Fact]
    public void A_permanent_http_failure_keeps_the_http_code()
    {
        var error = new HttpDownloadException("HTTP 404 (NotFound).", isTransient: false);

        Assert.False(error.IsTransient);
        Assert.Equal(DownloadExitCode.HttpPermanentFailure, error.ExitCode);
        Assert.Equal(4, error.ExitCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DownloadExitCode.HttpPermanentFailure)]
    [InlineData(HttpStatusCode.Forbidden, DownloadExitCode.HttpPermanentFailure)]
    [InlineData(HttpStatusCode.Gone, DownloadExitCode.HttpPermanentFailure)]
    [InlineData(HttpStatusCode.RequestTimeout, DownloadExitCode.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, DownloadExitCode.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, DownloadExitCode.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DownloadExitCode.TransientFailure)]
    public async Task A_probe_failure_carries_the_code_that_matches_its_flag(
        HttpStatusCode status,
        int expectedCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        });

        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpDownloadException>(
            () => Probe.ProbeAsync(client, ProbeUrl, new RequestOptions(), default));

        Assert.Equal(expectedCode == DownloadExitCode.TransientFailure, error.IsTransient);
        Assert.Equal(expectedCode, error.ExitCode);
    }

    [Fact]
    public void The_readme_table_and_the_constants_agree()
    {
        var documented = ReadDocumentedExitCodes();
        var constants = ExitCodeConstants();

        Assert.NotEmpty(constants);

        foreach (var (code, name) in constants)
        {
            Assert.True(
                documented.Any(row => row.Code == code),
                $"DownloadExitCode.{name} = {code} is not documented in the README 退出码 table. " +
                "Adding a code without a row leaves CLI users guessing what they just got.");
        }

        foreach (var row in documented)
        {
            Assert.True(
                constants.Any(entry => entry.Code == row.Code),
                $"README.md documents exit code {row.Code} (\"{row.Meaning}\") but DownloadExitCode has no " +
                "constant with that value, so the table is promising something the code cannot return.");
            Assert.False(
                string.IsNullOrWhiteSpace(row.Meaning),
                $"README.md documents exit code {row.Code} with an empty meaning.");
        }

        Assert.Equal(constants.Count, documented.Count);
    }

    /// <summary>Public integer constants declared on <see cref="DownloadExitCode"/>.</summary>
    private static List<(int Code, string Name)> ExitCodeConstants()
    {
        var constants = typeof(DownloadExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(int))
            .Select(field => ((int)field.GetRawConstantValue()!, field.Name))
            .ToList();

        // Two constants sharing a number would make the table ambiguous and the mapping
        // non-deterministic to read.
        var duplicate = constants
            .GroupBy(entry => entry.Item1)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Assert.Fail(
                $"DownloadExitCode declares {string.Join(", ", duplicate.Select(entry => entry.Item2))} " +
                $"with the same value {duplicate.Key}.");
        }

        return constants;
    }

    /// <summary>
    /// Parses the markdown table under the <c>### 退出码</c> heading.
    /// </summary>
    private static List<(int Code, string Meaning)> ReadDocumentedExitCodes()
    {
        var lines = File.ReadAllLines(RepoRoot.Resolve("README.md"));
        var rows = new List<(int Code, string Meaning)>();
        var inSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                inSection = string.Equals(trimmed, "### 退出码", StringComparison.Ordinal);
                continue;
            }

            if (!inSection || !trimmed.StartsWith('|'))
            {
                continue;
            }

            var cells = trimmed.Trim('|').Split('|');
            if (cells.Length < 2)
            {
                continue;
            }

            // The header row and the |---|---| separator do not parse as an integer.
            if (int.TryParse(cells[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                rows.Add((code, cells[1].Trim()));
            }
        }

        Assert.NotEmpty(rows);

        var duplicate = rows.GroupBy(row => row.Code).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Assert.Fail($"README.md documents exit code {duplicate.Key} more than once.");
        }

        return rows;
    }
}
