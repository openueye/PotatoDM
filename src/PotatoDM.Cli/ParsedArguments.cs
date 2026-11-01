using System.Globalization;

namespace PotatoDM.Cli;

/// <summary>Parses CLI switches while preserving positional arguments and aliases.</summary>
internal sealed class ParsedArguments
{
    public List<string> Positionals { get; } = new();
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "output", "connections", "min-segment-size", "state", "stall-timeout", "max-retries", "max-retry-delay", "max-per-host", "max-connections-per-job", "global-connections", "max-speed",
        "referer", "cookie", "user-agent", "ffmpeg", "port", "max-upload"
    };

    private static readonly HashSet<string> BooleanOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "json", "quiet", "restart", "restart-on-change", "dynamic-segments", "sparse", "no-proxy", "seed", "no-dht", "no-pex",
        "help"
    };

    public static ParsedArguments Parse(string[] args)
    {
        var parsed = new ParsedArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--")
            {
                parsed.Positionals.AddRange(args[(index + 1)..]);
                break;
            }

            if (!argument.StartsWith('-') || argument == "-")
            {
                parsed.Positionals.Add(argument);
                continue;
            }

            string name;
            string? value = null;
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = argument.IndexOf('=');
                name = separator < 0 ? argument[2..] : argument[2..separator];
                value = separator < 0 ? null : argument[(separator + 1)..];
            }
            else
            {
                name = argument switch
                {
                    "-o" => "output",
                    "-c" => "connections",
                    "-q" => "quiet",
                    "-h" => "help",
                    _ => throw new ArgumentException("unknown option '" + argument + "'"),
                };
            }

            if (ValueOptions.Contains(name))
            {
                if (value is null)
                {
                    if (++index >= args.Length)
                    {
                        throw new ArgumentException("option '--" + name + "' requires a value");
                    }

                    value = args[index];
                }
            }
            else
            {
                if (!BooleanOptions.Contains(name))
                {
                    throw new ArgumentException("unknown option '--" + name + "'");
                }

                if (value is not null)
                {
                    throw new ArgumentException("option '--" + name + "' does not take a value");
                }

                value = "true";
            }

            parsed._options[name] = value;
        }

        return parsed;
    }

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public bool GetBool(string name) =>
        string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public long GetLong(string name, long fallback) =>
        long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
