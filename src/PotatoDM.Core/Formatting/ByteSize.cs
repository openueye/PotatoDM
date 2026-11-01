namespace PotatoDM.Core.Formatting;

/// <summary>Formats byte counts and transfer rates for the UI and CLI.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.0} {Units[unit]}";
    }

    public static string Format(long? bytes, string nullPlaceholder = "?") =>
        bytes is { } value ? Format(value) : nullPlaceholder;

    public static string FormatSpeed(double bytesPerSecond) => Format((long)bytesPerSecond) + "/s";
}
