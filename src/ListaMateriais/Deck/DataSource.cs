using System.Globalization;
using System.Text;
using ListaMateriais.Kmz;

namespace ListaMateriais.Deck;

/// <summary>A table of data: column names and rows of text (null for empty cells).</summary>
/// <param name="Truncated">The file had more rows than are read; the presentation says so.</param>
internal sealed record DataSet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<string?[]> Rows, bool Truncated = false)
{
    public string? Cell(int row, int column) =>
        column < Rows[row].Length && !string.IsNullOrWhiteSpace(Rows[row][column]) ? Rows[row][column]!.Trim() : null;
}

/// <summary>
/// Reads the tables of a spreadsheet (.xlsx) or a .csv. The header row is found even when it is not the
/// first one (titles above the table are common), and the sheet that is the real table is picked.
/// </summary>
internal static class DataSource
{
    private const int MaxCsvRows = 200_000;
    private const int HeaderSearch = 15;

    /// <exception cref="AppException">The file cannot be read.</exception>
    public static IReadOnlyList<DataSet> Read(string path)
    {
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var csv = ReadCsv(path);
            return csv is null ? [] : [csv];
        }
        var sets = XlsxTable.Read(path).Select(t => FromRows(t.Name, t.Rows, t.Rows.Count >= XlsxTable.MaxRows)).OfType<DataSet>().ToList();
        return Combine(sets, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Sheets with the same columns (one per city, one per seller) are one table split up: they are joined, with the
    /// sheet name as a new column, and offered first. The column is named after the file when it says what the sheets
    /// are ("…_por_cidade" → "Cidade"), else "Aba".
    /// </summary>
    internal static List<DataSet> Combine(List<DataSet> sets, string fileName)
    {
        var groups = sets.Where(s => !IsSummaryName(s.Name))
            .GroupBy(s => string.Join('|', s.Headers.Select(AddressText.Plain)))
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Sum(s => s.Rows.Count))
            .ToList();
        if (groups.Count == 0)
            return sets;
        var group = groups[0].ToList();
        var plainFile = AddressText.Plain(fileName.Replace('_', ' '));
        var named = new[] { "CIDADE", "MUNICIPIO", "VENDEDOR", "TECNICO", "REGIAO", "LOJA", "FILIAL", "EQUIPE", "CLIENTE", "PRODUTO", "MES", "ANO" }
            .FirstOrDefault(w => plainFile.Contains(w, StringComparison.Ordinal));
        var column = named is null ? "Aba" : char.ToUpperInvariant(named[0]) + named[1..].ToLowerInvariant().Replace("municipio", "município", StringComparison.Ordinal);
        var headers = group[0].Headers.ToList();
        // A column that already holds the sheet's name is not repeated.
        var already = Enumerable.Range(0, headers.Count).Any(c => group.All(s => s.Rows.Take(20).All(r => c < r.Length && r[c] is { } v && AddressText.Plain(v) == AddressText.Plain(s.Name.Trim()))));
        while (headers.Contains(column, StringComparer.OrdinalIgnoreCase))
            column += " (aba)";
        var rows = new List<string?[]>();
        foreach (var set in group)
        {
            foreach (var row in set.Rows)
            {
                var copy = new string?[headers.Count + (already ? 0 : 1)];
                Array.Copy(row, copy, Math.Min(row.Length, headers.Count));
                if (!already)
                    copy[^1] = set.Name.Trim();
                rows.Add(copy);
            }
        }
        if (!already)
            headers.Add(column);
        var name = $"{group.Count} abas juntas ({string.Join(", ", group.Take(3).Select(s => s.Name.Trim()))}{(group.Count > 3 ? "…" : string.Empty)})";
        var joined = new DataSet(name, headers, rows, group.Any(s => s.Truncated));
        return [joined, .. sets];
    }

    /// <summary>The sheet most likely to be the data: many rows and columns, and not a summary.</summary>
    public static DataSet? Best(IReadOnlyList<DataSet> sets) =>
        sets.Where(s => s.Rows.Count > 0)
            .MaxBy(s => s.Rows.Count * Math.Min(s.Headers.Count, 12) * (IsSummaryName(s.Name) ? 0.05 : 1) * (s.Name.Contains(" abas juntas", StringComparison.Ordinal) ? 2 : 1));

    private static bool IsSummaryName(string name)
    {
        var plain = AddressText.Plain(name);
        return new[] { "RESUMO", "SUMARIO", "INDICADOR", "DASHBOARD", "CAPA", "GRAFICO", "PAINEL", "INSTRUC", "LEGENDA" }
            .Any(word => plain.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>The table in the rows: from the header row found on, without the blank and the title rows.</summary>
    internal static DataSet? FromRows(string name, IReadOnlyList<string?[]> rows, bool truncated = false)
    {
        if (rows.Count < 2)
            return null;
        var header = HeaderRow(rows);
        var width = rows.Skip(header).Take(200).Max(r => r.Length);
        var titles = rows[header];
        var headers = new List<string>();
        for (var c = 0; c < width; c++)
        {
            var title = c < titles.Length && !string.IsNullOrWhiteSpace(titles[c]) ? Clean(titles[c]!) : $"Coluna {SheetTable.ColumnLetter(c)}";
            var unique = title;
            for (var n = 2; headers.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
                unique = $"{title} ({n})";
            headers.Add(unique);
        }
        var data = rows.Skip(header + 1).Where(r => r.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToList();
        return data.Count == 0 ? null : new DataSet(name, headers, data, truncated);
    }

    /// <summary>
    /// The first row that looks like column names: mostly text, with filled rows below it in the same columns.
    /// Titles above the table have only one or two cells and lose to it.
    /// </summary>
    private static int HeaderRow(IReadOnlyList<string?[]> rows)
    {
        var best = 0;
        var bestScore = double.MinValue;
        for (var r = 0; r < Math.Min(HeaderSearch, rows.Count - 1); r++)
        {
            var cells = Enumerable.Range(0, rows[r].Length).Where(c => !string.IsNullOrWhiteSpace(rows[r][c])).ToList();
            if (cells.Count < 2)
                continue;
            var text = cells.Count(c => !Numbers.TryParse(rows[r][c], out _, out _) && !Dates.TryParse(rows[r][c], false, out _));
            var below = rows.Skip(r + 1).Take(10).ToList();
            var filledBelow = cells.Count(c => below.Count(row => c < row.Length && !string.IsNullOrWhiteSpace(row[c])) >= below.Count / 2.0);
            var score = text * 2 + filledBelow - (cells.Count - text) * 3 - r * 0.1;
            if (text >= cells.Count * 0.7 && score > bestScore)
            {
                best = r;
                bestScore = score;
            }
        }
        return best;
    }

    private static string Clean(string title) => string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DataSet? ReadCsv(string path)
    {
        byte[] bytes;
        try
        {
            bytes = Files.ReadAllBytesShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AppException($"Não foi possível abrir \"{Path.GetFileName(path)}\".\n\nDetalhe: {e.Message}", e);
        }
        // UTF-8 when it decodes cleanly; otherwise the Windows codepage Excel uses for CSV (Latin-1 covers Portuguese).
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        text = text.TrimStart('﻿');
        var firstLine = text.AsSpan(0, Math.Min(text.Length, 4000)).ToString().Split('\n')[0];
        var separator = new[] { ';', ',', '\t', '|' }.MaxBy(c => firstLine.Count(x => x == c));
        var rows = new List<string?[]>();
        var truncated = false;
        foreach (var row in CsvRows(text, separator))
        {
            if (rows.Count >= MaxCsvRows)
            {
                truncated = true;
                break;
            }
            rows.Add(row);
        }
        return FromRows(Path.GetFileNameWithoutExtension(path), rows, truncated);
    }

    /// <summary>Splits CSV text into rows, with quoted fields that may hold separators, quotes ("") and line breaks.</summary>
    internal static IEnumerable<string?[]> CsvRows(string text, char separator)
    {
        var fields = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(Field(field));
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(Field(field));
                yield return [.. fields];
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(Field(field));
            yield return [.. fields];
        }
    }

    private static string? Field(StringBuilder field)
    {
        var value = field.ToString().Trim();
        field.Clear();
        return value.Length == 0 ? null : value;
    }
}

/// <summary>Numbers as people write them in Brazilian sheets: "1.234,56", "R$ 500,00", "12%", "1234.5", "-".</summary>
internal static class Numbers
{
    public static bool TryParse(string? text, out double value, out bool money) => TryParse(text, out value, out money, out _);

    public static bool TryParse(string? text, out double value, out bool money, out bool percent)
    {
        value = 0;
        money = false;
        percent = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim().Replace(' ', ' ');
        if (s.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
        {
            money = true;
            s = s[2..].Trim();
        }
        else if (s.StartsWith("-R$", StringComparison.OrdinalIgnoreCase) || s.StartsWith("- R$", StringComparison.OrdinalIgnoreCase))
        {
            money = true;
            s = "-" + s[(s.IndexOf('$', StringComparison.Ordinal) + 1)..].Trim();
        }
        if (s.EndsWith('%'))
        {
            percent = true;
            s = s[..^1].Trim();
        }
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative)
            s = s[1..^1];
        s = s.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (s.Length == 0 || s.Any(c => !(char.IsAsciiDigit(c) || c is '.' or ',' or '-' or '+' or 'E' or 'e')))
            return false;
        if (!s.Any(char.IsAsciiDigit))
            return false;

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Both: the last one is the decimal separator.
            s = lastComma > lastDot ? s.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.') : s.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (lastComma >= 0)
        {
            // "1,234,567" is thousands; "12,5" is a decimal.
            s = s.Count(c => c == ',') > 1 ? s.Replace(",", string.Empty, StringComparison.Ordinal) : s.Replace(',', '.');
        }
        else if (s.Count(c => c == '.') > 1)
        {
            s = s.Replace(".", string.Empty, StringComparison.Ordinal);
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
            return false;
        if (negative)
            value = -value;
        if (percent)
            value /= 100;
        return true;
    }
}

/// <summary>Dates as they come out of sheets: Excel serial numbers, "31/12/2026", "2026-12-31", "12/2026", "dez/26".</summary>
internal static class Dates
{
    private static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    private static readonly string[] Formats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm", "dd/MM/yy", "d/M/yy",
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss.fff",
        "dd-MM-yyyy", "dd.MM.yyyy", "MM/yyyy", "M/yyyy", "yyyy/MM/dd", "yyyy-MM",
    ];

    /// <param name="serials">Whether a plain number is an Excel date (only for columns whose title says date).</param>
    public static bool TryParse(string? text, bool serials, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        if (serials && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            // Excel counts days from 1899-12-30; 20000–80000 is 1954–2119.
            if (serial is >= 20000 and <= 80000)
            {
                date = DateTime.FromOADate(Math.Floor(serial));
                return true;
            }
            return false;
        }
        if (s.Length < 6 || !s.Any(char.IsAsciiDigit))
            return MonthName(s, out date);
        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
            return date.Year is >= 1950 and <= 2150;
        return MonthName(s, out date);
    }

    /// <summary>"jan/26", "janeiro de 2026", "JAN-2026".</summary>
    private static bool MonthName(string s, out DateTime date)
    {
        date = default;
        var plain = AddressText.Plain(s);
        var parts = plain.Split([' ', '/', '-', '.'], StringSplitOptions.RemoveEmptyEntries).Where(p => p != "DE").ToArray();
        if (parts.Length != 2 || parts[0].Length < 3)
            return false;
        var month = Array.FindIndex(Months, m => parts[0].StartsWith(m, StringComparison.Ordinal));
        if (month < 0 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return false;
        year = year < 100 ? 2000 + year : year;
        if (year is < 1950 or > 2150)
            return false;
        date = new DateTime(year, month + 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }
}
