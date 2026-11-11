using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ListaMateriais.Kmz;

/// <summary>
/// Reads points typed or pasted by the user: a Python dict / list, or plain lines such as
/// "Nome, -23.5505, -46.6333" (one point per line; the name is optional and may come last).
/// </summary>
internal static partial class PointText
{
    private static readonly HashSet<string> LatitudeKeys = new(StringComparer.Ordinal) { "LAT", "LATITUDE", "Y" };
    private static readonly HashSet<string> LongitudeKeys = new(StringComparer.Ordinal) { "LON", "LNG", "LONG", "LONGITUDE", "X" };
    private static readonly HashSet<string> NameKeys = new(StringComparer.Ordinal)
    {
        "NOME", "NAME", "TITULO", "TITLE", "ID", "LABEL", "ROTULO", "PONTO", "DESCRICAO",
    };
    private static readonly HashSet<string> PairKeys = new(StringComparer.Ordinal)
    {
        "COORDENADAS", "COORDENADA", "COORDS", "COORD", "COORDINATES", "POSICAO", "POSITION", "LOCATION", "LOCAL", "LATLON", "LATLNG",
    };

    public static bool IsLatitudeKey(string text) => LatitudeKeys.Contains(Key(text));

    public static bool IsLongitudeKey(string text) => LongitudeKeys.Contains(Key(text));

    public static bool IsNameKey(string text) => NameKeys.Contains(Key(text));

    public static PointList Parse(string text, CoordinateOrder order)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PointList.Empty;
        var collector = new PointCollector(order);
        if (LooksLikeLiteral().IsMatch(text))
            ReadLiteral(text, collector);
        else
            ReadLines(text, collector);
        return collector.Result;
    }

    // ---- Python dict / list --------------------------------------------------------------------

    private static void ReadLiteral(string text, PointCollector points, string? line = null)
    {
        object? value;
        try
        {
            value = PyLiteral.Parse(text);
        }
        catch (FormatException e)
        {
            // Inside a single line the parser's own "Linha 1" would point at the wrong place.
            points.Problem(line ?? "Dicionário", line is null ? e.Message : e.Message[(e.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
            return;
        }

        switch (value)
        {
            case PyDict dict when TryPoint(dict, null, "Dicionário", points):
                break;
            case PyDict dict when TryColumns(dict, points):
                break;
            case PyDict dict:
                foreach (var (key, item) in dict.Entries)
                    ReadEntry(key, item, points);
                break;
            case List<object?> list when IsPair(list) || IsNamedPair(list):
                ReadItem(list, "Entrada", points);
                break;
            case List<object?> list:
                for (var i = 0; i < list.Count; i++)
                    ReadItem(list[i], $"Item {i + 1}", points);
                break;
            default:
                points.Problem("Dicionário", "esperava um dicionário { } ou uma lista [ ].");
                break;
        }
    }

    /// <summary>One "key: value" of the dict: usually name → coordinates.</summary>
    private static void ReadEntry(object? key, object? value, PointCollector points)
    {
        var where = $"\"{Show(key)}\"";
        switch (value)
        {
            case List<object?> pair when IsPair(pair):
                points.AddPair(where, Show(key), Number(pair[0]), Number(pair[1]));
                break;
            case string text when TryPairText(text, out var first, out var second):
                points.AddPair(where, Show(key), first, second);
                break;
            case PyDict inner when TryPoint(inner, Show(key), where, points):
                break;
            // (lat, lon): "Nome" also happens.
            case string name when key is List<object?> pair && IsPair(pair):
                points.AddPair($"\"{name}\"", name, Number(pair[0]), Number(pair[1]));
                break;
            default:
                points.Problem(where, "não achei um par de coordenadas (ex.: (-23.55, -46.63)).");
                break;
        }
    }

    /// <summary>One element of a list: a dict with lat/lon, or (name, lat, lon) / (lat, lon, name) / (lat, lon).</summary>
    private static void ReadItem(object? item, string where, PointCollector points)
    {
        switch (item)
        {
            case PyDict dict when TryPoint(dict, null, where, points):
                break;
            case List<object?> pair when IsPair(pair):
                points.AddPair(where, null, Number(pair[0]), Number(pair[1]));
                break;
            case List<object?> { Count: 3 } named when IsNamedPair(named):
                if (IsNumber(named[0]) && IsNumber(named[1]))
                    points.AddPair(where, Show(named[2]), Number(named[0]), Number(named[1]));
                else
                    points.AddPair(where, Show(named[0]), Number(named[1]), Number(named[2]));
                break;
            case string text when TryPairText(text, out var first, out var second):
                points.AddPair(where, null, first, second);
                break;
            default:
                points.Problem(where, "não achei um par de coordenadas.");
                break;
        }
    }

    /// <summary>A dict that is itself one point: {"nome": "A", "lat": -23.5, "lon": -46.6} or {"nome": "A", "coordenadas": (..)}.</summary>
    private static bool TryPoint(PyDict dict, string? fallbackName, string where, PointCollector points)
    {
        object? latitude = null, longitude = null, pair = null, name = null;
        foreach (var (key, value) in dict.Entries)
        {
            if (key is not string text)
                continue;
            var normalized = Key(text);
            if (LatitudeKeys.Contains(normalized))
                latitude = value;
            else if (LongitudeKeys.Contains(normalized))
                longitude = value;
            else if (PairKeys.Contains(normalized))
                pair = value;
            else if (NameKeys.Contains(normalized))
                name ??= value;
        }
        var label = name is null ? fallbackName : Show(name);

        if (latitude is not null && longitude is not null && IsNumber(latitude) && IsNumber(longitude))
        {
            points.Add(where, label, Number(latitude), Number(longitude));
            return true;
        }
        if (pair is List<object?> list && IsPair(list))
        {
            points.AddPair(where, label, Number(list[0]), Number(list[1]));
            return true;
        }
        if (pair is string written && TryPairText(written, out var first, out var second))
        {
            points.AddPair(where, label, first, second);
            return true;
        }
        return false;
    }

    /// <summary>Column-wise data, as in a pandas dict: {"nome": [...], "lat": [...], "lon": [...]}.</summary>
    private static bool TryColumns(PyDict dict, PointCollector points)
    {
        List<object?>? latitudes = null, longitudes = null, names = null;
        foreach (var (key, value) in dict.Entries)
        {
            if (key is not string text || value is not List<object?> column)
                continue;
            var normalized = Key(text);
            if (LatitudeKeys.Contains(normalized))
                latitudes = column;
            else if (LongitudeKeys.Contains(normalized))
                longitudes = column;
            else if (NameKeys.Contains(normalized))
                names ??= column;
        }
        if (latitudes is null || longitudes is null)
            return false;

        for (var i = 0; i < Math.Max(latitudes.Count, longitudes.Count); i++)
        {
            var where = $"Item {i + 1}";
            var name = names is not null && i < names.Count ? Show(names[i]) : null;
            if (i < latitudes.Count && i < longitudes.Count && IsNumber(latitudes[i]) && IsNumber(longitudes[i]))
                points.Add(where, name, Number(latitudes[i]), Number(longitudes[i]));
            else
                points.Problem(where, "latitude ou longitude faltando.");
        }
        return true;
    }

    private static bool IsPair(List<object?> list) => list.Count == 2 && IsNumber(list[0]) && IsNumber(list[1]);

    private static bool IsNamedPair(List<object?> list) =>
        list.Count == 3 && ((IsNumber(list[1]) && IsNumber(list[2]) && !IsNumber(list[0]) && list[0] is string)
                            || (IsNumber(list[0]) && IsNumber(list[1]) && list[2] is string));

    private static bool IsNumber(object? value) => value is double || (value is string text && Coordinate.TryParse(text, out _));

    private static double Number(object? value) =>
        value is double number ? number : Coordinate.TryParse(value as string, out var parsed) ? parsed : double.NaN;

    private static string Show(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        double number => number.ToString("0.########", CultureInfo.InvariantCulture),
        bool flag => flag ? "True" : "False",
        List<object?> list => "(" + string.Join(", ", list.Select(Show)) + ")",
        _ => "{...}",
    };

    // ---- Plain lines ---------------------------------------------------------------------------

    private static void ReadLines(string text, PointCollector points)
    {
        var lines = text.Split('\n');
        var single = lines.Count(l => !string.IsNullOrWhiteSpace(l)) == 1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var where = single ? "Entrada" : $"Linha {i + 1}";
            // A dict or tuple on a line of its own, among plain lines.
            if (line[0] is '{' or '[' or '(')
                ReadLiteral(line, points, where);
            else if (TryLine(line, out var name, out var first, out var second))
                points.AddPair(where, name, first, second);
            else
                points.Problem(where, "esperava \"nome, latitude, longitude\" (o nome é opcional).");
        }
    }

    private static bool TryLine(string line, out string? name, out double first, out double second)
    {
        // Just the two numbers, however they are separated: "-23.55 -46.63", "-23,55, -46,63", "-23.55;-46.63".
        var words = line.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',')).ToList();
        name = null;
        if (words.Count == 2 && Coordinate.TryParse(words[0], out first) && Coordinate.TryParse(words[1], out second))
            return true;

        var separator = line.Contains(';') ? ';' : line.Contains('\t') ? '\t' : ',';
        var tokens = line.Split(separator).Select(t => t.Trim()).ToList();
        // "-23.55 -46.63" or "Poste 12 -23.55 -46.63": no separator at all, so split on spaces.
        if (tokens.Count == 1)
            tokens = [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        return TryTokens(tokens, separator == ',' && line.Contains(','), out name, out first, out second);
    }

    /// <summary>The coordinates are at the start or at the end; whatever is left is the name.</summary>
    private static bool TryTokens(IReadOnlyList<string> tokens, bool commas, out string? name, out double first, out double second)
    {
        name = null;
        first = second = 0;
        var count = tokens.Count;
        if (count < 2)
            return false;
        var separator = commas ? ", " : " ";

        // Decimal commas split each number in two: "-23,5505, -46,6333".
        if (commas && count >= 4)
        {
            if (TryDecimalCommas(tokens, count - 4, out first, out second) && (count == 4 || !IsNumeric(tokens[0])))
            {
                name = Join(tokens.Take(count - 4), separator);
                return true;
            }
            if (TryDecimalCommas(tokens, 0, out first, out second) && !IsNumeric(tokens[^1]))
            {
                name = Join(tokens.Skip(4), separator);
                return true;
            }
        }

        // The name comes first when both readings work ("12, -23.55, -46.63" is point "12").
        if (Coordinate.TryParse(tokens[^2], out first) && Coordinate.TryParse(tokens[^1], out second))
        {
            name = Join(tokens.Take(count - 2), separator);
            return true;
        }
        if (Coordinate.TryParse(tokens[0], out first) && Coordinate.TryParse(tokens[1], out second))
        {
            name = Join(tokens.Skip(2), separator);
            return true;
        }
        return false;
    }

    private static bool TryDecimalCommas(IReadOnlyList<string> tokens, int start, out double first, out double second)
    {
        first = second = 0;
        return WholePart().IsMatch(tokens[start]) && Digits().IsMatch(tokens[start + 1])
               && WholePart().IsMatch(tokens[start + 2]) && Digits().IsMatch(tokens[start + 3])
               && Coordinate.TryParse(tokens[start] + "." + tokens[start + 1], out first)
               && Coordinate.TryParse(tokens[start + 2] + "." + tokens[start + 3], out second);
    }

    /// <summary>"lat, lon" inside a string value.</summary>
    private static bool TryPairText(string text, out double first, out double second)
    {
        first = second = 0;
        return TryLine(text.Trim(), out var name, out first, out second) && string.IsNullOrEmpty(name);
    }

    private static bool IsNumeric(string token) => Coordinate.TryParse(token, out _);

    private static string? Join(IEnumerable<string> tokens, string separator) =>
        string.Join(separator, tokens.Where(t => t.Length > 0)) is { Length: > 0 } name ? name.Trim('"', '\'', ' ') : null;

    /// <summary>Upper case without accents, spaces or punctuation: "Descrição" and "descricao" are the same key.</summary>
    private static string Key(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
                result.Append(char.ToUpperInvariant(c));
        }
        return result.ToString();
    }

    [GeneratedRegex(@"^\s*(?:[\p{L}_][\p{L}\p{Nd}_]*\s*=\s*)?[\{\[\(]")]
    private static partial Regex LooksLikeLiteral();

    [GeneratedRegex(@"^[-+]?\d{1,3}$")]
    private static partial Regex WholePart();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();
}
