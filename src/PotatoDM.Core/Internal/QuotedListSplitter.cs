using System.Text;

namespace PotatoDM.Core.Internal;

/// <summary>Splits header/playlist lists without breaking on separators inside quotes.</summary>
internal static class QuotedListSplitter
{
    public static IEnumerable<string> Split(string text, char separator)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == separator && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
