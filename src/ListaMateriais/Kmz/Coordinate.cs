using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais.Kmz;

/// <summary>Reads one coordinate written the way people paste it: "-23.5505", "-23,5505", "23.55° S", 23°33'01.8"S.</summary>
internal static partial class Coordinate
{
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var match = Pattern().Match(text.Trim());
        if (!match.Success)
            return false;

        if (!TryNumber(match.Groups["deg"].Value, out var degrees))
            return false;
        double minutes = 0, seconds = 0;
        if (match.Groups["min"].Success && !TryNumber(match.Groups["min"].Value, out minutes))
            return false;
        if (match.Groups["sec"].Success && !TryNumber(match.Groups["sec"].Value, out seconds))
            return false;
        if (minutes >= 60 || seconds >= 60)
            return false;

        value = degrees + minutes / 60 + seconds / 3600;
        var hemisphere = (match.Groups["h1"].Value + match.Groups["h2"].Value).ToUpperInvariant();
        // S (sul) and W / O (oeste) are the negative halves; L (leste) is east.
        if (match.Groups["sign"].Value == "-" || hemisphere is "S" or "W" or "O")
            value = -value;
        return double.IsFinite(value);
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(
        @"^(?<h1>[NSEWLO])?\s*(?<sign>[-+])?\s*(?<deg>\d+(?:[.,]\d+)?)\s*[°º]?\s*" +
        @"(?:(?<min>\d+(?:[.,]\d+)?)\s*['′’]\s*(?:(?<sec>\d+(?:[.,]\d+)?)\s*(?:""|″|”|'')\s*)?)?" +
        @"(?<h2>[NSEWLO])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
