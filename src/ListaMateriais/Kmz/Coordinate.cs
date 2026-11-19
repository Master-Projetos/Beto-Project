
using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais.Kmz;

/// <summary>
/// Reads coordinates in decimal or DMS format.
/// Examples:
/// -23.5505
/// -23,5505
/// 23.55° S
/// 23°33'01.8"S
/// </summary>
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

        var hemisphere1 = match.Groups["h1"].Value.ToUpperInvariant();
        var hemisphere2 = match.Groups["h2"].Value.ToUpperInvariant();
        var sign = match.Groups["sign"].Value;

        // Não permite dois hemisférios simultâneos.
        if (!string.IsNullOrEmpty(hemisphere1) &&
            !string.IsNullOrEmpty(hemisphere2))
            return false;

        var hemisphere = hemisphere1 + hemisphere2;

        bool negativeHemisphere = hemisphere is "S" or "W" or "O";
        bool positiveHemisphere = hemisphere is "N" or "E" or "L";

        // Impede combinações contraditórias.
        if (sign == "-" && positiveHemisphere)
            return false;

        if (sign == "+" && negativeHemisphere)
            return false;

        var degreeText = match.Groups["deg"].Value;

        // Graus decimais não devem ser misturados com minutos/segundos.
        if ((match.Groups["min"].Success || match.Groups["sec"].Success) &&
            (degreeText.Contains('.') || degreeText.Contains(',')))
            return false;

        if (!TryNumber(degreeText, out var degrees))
            return false;

        double minutes = 0;
        double seconds = 0;

        if (match.Groups["min"].Success &&
            !TryNumber(match.Groups["min"].Value, out minutes))
            return false;

        if (match.Groups["sec"].Success &&
            !TryNumber(match.Groups["sec"].Value, out seconds))
            return false;

        // Minutos e segundos precisam estar entre 0 e 59.
        if (minutes >= 60 || seconds >= 60)
            return false;

        value = degrees + minutes / 60 + seconds / 3600;

        // Aplica sinal negativo quando necessário.
        if (sign == "-" || negativeHemisphere)
            value = -value;

        return double.IsFinite(value);
    }

    private static bool TryNumber(string text, out double value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        return double.TryParse(
            text.Replace(',', '.'),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value
        ) && double.IsFinite(value);
    }

    /// <summary>
    /// Reads a decimal number with typing slips, for when <see cref="TryParse"/> fails: the separator
    /// doubled ("-20..29481") or a stray mark among the digits ("-2'0.30591"). Text with letters or
    /// more than one decimal separator is not guessed at.
    /// </summary>
    public static bool TryRepair(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsLetter))
            return false;
        var cleaned = StrayMarks().Replace(text, string.Empty);
        cleaned = RepeatedSeparators().Replace(cleaned, separator => separator.Value[..1]);
        return cleaned != text.Trim() && PlainNumber().IsMatch(cleaned) && TryParse(cleaned, out value);
    }

    [GeneratedRegex(@"[\s'`´""′″‘’“”°º;]")]
    private static partial Regex StrayMarks();

    [GeneratedRegex(@"[.,]{2,}")]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex(@"^[-+]?\d+(?:[.,]\d+)?$")]
    private static partial Regex PlainNumber();

    [GeneratedRegex(
        @"^(?<h1>[NSEWLO])?\s*(?<sign>[-+])?\s*(?<deg>\d+(?:[.,]\d+)?)\s*[°º]?\s*" +
        @"(?:(?<min>\d+(?:[.,]\d+)?)\s*['′’]\s*(?:(?<sec>\d+(?:[.,]\d+)?)\s*(?:""|″|”|'')\s*)?)?" +
        @"(?<h2>[NSEWLO])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
