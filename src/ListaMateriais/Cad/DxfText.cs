using System.Text;
using System.Text.RegularExpressions;

namespace ListaMateriais.Cad;

/// <summary>Cleans the formatting codes out of the text of a drawing (MTEXT: fonts, colors, line breaks).</summary>
internal static partial class DxfText
{
    [GeneratedRegex(@"\\[fFCcHhQqTtWwAaLlOoKk][^;]*;")]
    private static partial Regex Codes();

    public static string Plain(string text)
    {
        var s = text.Replace("\\P", " ", StringComparison.Ordinal).Replace("\\p", " ", StringComparison.Ordinal)
            .Replace("\\~", " ", StringComparison.Ordinal).Replace("\\\\", "\u0001", StringComparison.Ordinal)
            .Replace("\\{", "\u0002", StringComparison.Ordinal).Replace("\\}", "\u0003", StringComparison.Ordinal);
        s = Codes().Replace(s, string.Empty);
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '{' or '}':
                    break;
                case '\u0001':
                    builder.Append('\\');
                    break;
                case '\u0002':
                    builder.Append('{');
                    break;
                case '\u0003':
                    builder.Append('}');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString().Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase).Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase).Trim();
    }
}
