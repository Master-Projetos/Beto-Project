using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais;

/// <summary>A line of text of a PDF page. The position is optional: without it, wrapped cells cannot be put back together.</summary>
/// <param name="Baseline">Height of the line on the page, in points (PDF: grows upward).</param>
/// <param name="Size">Font size, in points.</param>
/// <param name="Words">Where each word of <paramref name="Text"/> starts and its horizontal extent, left to right.</param>
internal sealed record TextLine(string Text, int Page = 0, double Baseline = 0, double Size = 0,
    IReadOnlyList<TextWord>? Words = null)
{
    public double Right => Words is { Count: > 0 } ? Words.Max(w => w.Right) : 0;

    /// <summary>Left edge of the word that holds the character at <paramref name="index"/>.</summary>
    public double LeftAt(int index) => Words!.LastOrDefault(w => w.Start <= index, Words![0]).Left;
}

/// <param name="Start">Index of the word's first character in <see cref="TextLine.Text"/>.</param>
internal readonly record struct TextWord(int Start, double Left, double Right);

/// <summary>
/// Reads the material tables of a TCE/TCR that was saved as PDF (the Word document itself, not the
/// Route Map report). The tables keep the layout of the Word ones: a first row with the title and the
/// column names, then "name, unit, quantity, unit price, total" rows and a TOTAL line.
/// </summary>
/// <remarks>
/// A long name wraps inside its cell and comes out as extra lines holding only a piece of the name.
/// With the position of the lines those pieces are given back to the row above or below, whichever
/// is closer (a cell boundary leaves a bigger gap than two lines of the same cell).
/// </remarks>
internal static partial class TcePdfParser
{
    private const double PriceTolerance = 0.02;

    /// <summary>A header with no item below it within this many lines was just text that looked like one.</summary>
    private const int MaxLinesBeforeFirstItem = 6;

    private const int MaxTitleLength = 80;

    private static readonly HashSet<string> UnitWords = new(StringComparer.Ordinal) { "UN", "UND", "UNID", "UNIDADE", "UM" };
    private static readonly HashSet<string> QuantityWords = new(StringComparer.Ordinal) { "QTD", "QTDE", "QT", "QUANT", "QUANTIDADE" };
    private static readonly HashSet<string> PriceWords = new(StringComparer.Ordinal)
    {
        "VALOR", "VLR", "PREÇO", "PRECO", "UNIT", "UNITÁRIO", "UNITARIO",
    };

    /// <param name="TailStart">Index in the line where the columns after the name start.</param>
    /// <param name="Checked">Quantity × unit price matches the printed total.</param>
    private sealed record Row(string Name, string Unit, double Quantity, double UnitPrice, double? Total, bool HasPrice,
        bool Checked, int TailStart);

    private sealed class Item(Row row, TextLine line)
    {
        public Row Row { get; } = row;
        public TextLine Line { get; } = line;
        public string Name { get; set; } = row.Name;
    }

    private sealed class Table(string title, TextLine? header, double boundary)
    {
        public string Title { get; set; } = title;

        /// <summary>The line with the column names; null when the table has none.</summary>
        public TextLine? Header { get; } = header;

        /// <summary>Where the name column of the header ends.</summary>
        public double Boundary { get; } = boundary;

        /// <summary>Short text right above the header that may be the start of a wrapped title.</summary>
        public string? TitleStart { get; init; }

        public List<Item> Items { get; } = [];
        public List<TextLine> Pieces { get; } = [];
        public int LinesWithoutItem { get; set; }
        public bool NamesWrapUpward { get; set; }
    }

    /// <summary>Parses plain lines, top to bottom. Wrapped names are not rebuilt.</summary>
    /// <param name="fileName">Used for the project code when the text does not have it.</param>
    public static DocumentContent Parse(IReadOnlyList<string> lines, string? fileName = null,
        IReadOnlyCollection<string>? knownTitles = null) =>
        Parse(lines.Select(l => new TextLine(Whitespace().Replace(l, " ").Trim())).ToList(), fileName, knownTitles);

    /// <summary>Parses the lines of the document, top to bottom.</summary>
    /// <param name="knownTitles">Normalized titles the user registered; they settle whether a title wrapped.</param>
    public static DocumentContent Parse(IReadOnlyList<TextLine> lines, string? fileName = null,
        IReadOnlyCollection<string>? knownTitles = null)
    {
        var tables = new List<MaterialTable>();
        var warnings = new List<string>();
        Table? open = null;
        TextLine? lastText = null;

        void Close()
        {
            if (open is null)
                return;
            GivePieces(open, next: null);
            Finish(open, knownTitles, tables, warnings);
            open = null;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Text.Length == 0)
                continue;

            if (TryHeader(line.Text, out var headerTitle, out var firstColumn))
            {
                Close();
                var boundary = line.Words is null ? 0 : line.LeftAt(firstColumn);
                open = new Table(headerTitle ?? TitleFrom(lastText?.Text), line, boundary)
                {
                    TitleStart = headerTitle is null ? null : TitleStart(lines, i, boundary),
                };
                lastText = null;
                continue;
            }

            if (TotalLine().Match(line.Text) is { Success: true } total)
            {
                if (open is { Items.Count: > 0 } && total.Groups["value"].Success && total.Groups["value"].Value != "-")
                {
                    GivePieces(open, next: null);
                    CheckTotal(open, Money(total.Groups["value"].Value), warnings);
                }
                Close();
                lastText = null;
                continue;
            }

            // A line that starts past the name column has no name of its own.
            var nameless = open is not null && line.Words is { Count: > 0 } placed && Anchor(open)?.Words is not null
                           && placed[0].Left >= NameColumnEnd(open) - 2;
            if ((nameless ? TryRow(line.Text, nameless: true) ?? TryRow(line.Text) : TryRow(line.Text)) is { } row
                && IsRow(row, line, open))
            {
                open ??= new Table(TitleFrom(lastText?.Text), header: null, boundary: 0);
                var item = new Item(row, line);
                GivePieces(open, item);
                open.Items.Add(item);
                lastText = null;
                continue;
            }

            if (open is not null && IsPiece(line, open))
            {
                open.Pieces.Add(line);
                continue;
            }
            if (open is { Items.Count: > 0 })
                Close();
            else if (open is not null && ++open.LinesWithoutItem > MaxLinesBeforeFirstItem)
                open = null;
            lastText = line;
        }
        Close();

        return new DocumentContent(tables, ReadProjectInfo(lines, fileName), 0, warnings);
    }

    /// <summary>
    /// Outside a table only a named row whose quantity × price matches its total starts one. Inside,
    /// a row with no price must have its quantity out in the columns: "BAP 3" under a name is the
    /// rest of that name, not a material.
    /// </summary>
    private static bool IsRow(Row row, TextLine line, Table? table)
    {
        if (table is null)
            return row.Checked && row.Name.Length > 0;
        if (row.HasPrice)
            return row.Name.Length > 0 || line.Words is not null;
        if (row.Name.Length == 0 || row.Unit.Length == 0)
            return false;
        return line.Words is null || line.LeftAt(row.TailStart) >= NameColumnEnd(table) - 2;
    }

    /// <summary>The line the next piece of a wrapped name would hang from.</summary>
    private static TextLine? Anchor(Table table) =>
        table.Pieces.Count > 0 ? table.Pieces[^1] : table.Items.Count > 0 ? table.Items[^1].Line : table.Header;

    private static double NameColumnEnd(Table table) =>
        table.Items.Count > 0 ? table.Items[^1].Line.LeftAt(table.Items[^1].Row.TailStart) : table.Boundary;

    /// <summary>A line that sits inside the name column, right below the previous line of the table.</summary>
    private static bool IsPiece(TextLine line, Table table)
    {
        if (line.Words is null || Anchor(table) is not { Words: not null } above || above.Page != line.Page)
            return false;
        var drop = above.Baseline - line.Baseline;
        return drop > 0 && drop <= 2.5 * Math.Max(line.Size, 6) && line.Right <= NameColumnEnd(table) + 1;
    }

    /// <summary>
    /// Hands the pieces collected since the last row to the row above (the rest of its name) or to
    /// <paramref name="next"/> (the start of its name). The biggest gap between the lines is the cell
    /// boundary; evenly spaced lines hang from the row above, as in a top-aligned Word table.
    /// </summary>
    private static void GivePieces(Table table, Item? next)
    {
        var pieces = table.Pieces;
        if (pieces.Count == 0)
            return;

        var up = pieces.Count;
        var above = table.Items.Count > 0 ? table.Items[^1].Line : table.Header!;
        if (next is not null && next.Line.Words is not null && next.Line.Page == pieces[^1].Page)
        {
            var heights = pieces.Select(p => p.Baseline).Prepend(above.Baseline).Append(next.Line.Baseline).ToList();
            var gaps = heights.Zip(heights.Skip(1), (a, b) => a - b).ToList();
            if (gaps.Max() > gaps.Min() * 1.08 + 0.3)
                up = gaps.IndexOf(gaps.Max());
        }

        var rest = string.Join(' ', pieces.Take(up).Select(p => p.Text));
        if (rest.Length > 0 && table.Items.Count > 0)
            table.Items[^1].Name = (table.Items[^1].Name + " " + rest).Trim();
        else if (rest.Length > 0 && table.Title != MaterialTable.Untitled)
            table.Title = Config.Normalize(table.Title + " " + rest);

        if (up < pieces.Count)
        {
            next!.Name = (string.Join(' ', pieces.Skip(up).Select(p => p.Text)) + " " + next.Name).Trim();
            table.NamesWrapUpward = true;
        }
        pieces.Clear();
    }

    /// <summary>
    /// The lines right above the header that fit in its name column: in a bottom-aligned cell they are
    /// the first lines of the title. Null when there is none.
    /// </summary>
    private static string? TitleStart(IReadOnlyList<TextLine> lines, int header, double boundary)
    {
        if (lines[header].Words is null)
            return null;
        var start = new List<string>();
        var below = lines[header];
        for (var i = header - 1; i >= 0 && start.Count < 4; i--)
        {
            var line = lines[i];
            var rise = line.Baseline - below.Baseline;
            if (line.Words is null || line.Page != below.Page || rise <= 0 || rise > 1.6 * Math.Max(below.Size, 6)
                || line.Right > boundary + 1 || TryRow(line.Text) is { HasPrice: true } || TotalLine().IsMatch(line.Text))
                break;
            start.Insert(0, line.Text);
            below = line;
        }
        return start.Count > 0 ? string.Join(' ', start) : null;
    }

    private static void Finish(Table table, IReadOnlyCollection<string>? knownTitles, List<MaterialTable> tables,
        List<string> warnings)
    {
        // The title wrapped upward when the names of this table do, or when only the whole thing is a known title.
        if (table.TitleStart is { } start && Config.Normalize(start + " " + table.Title) is var whole
            && (table.NamesWrapUpward || (knownTitles?.Contains(whole) == true && !knownTitles.Contains(table.Title))))
            table.Title = whole;

        var items = new List<Occurrence>();
        foreach (var item in table.Items)
        {
            var row = item.Row;
            if (item.Name.Length == 0)
            {
                warnings.Add($"Linha sem nome de material ignorada em {table.Title}: \"{item.Line.Text}\".");
                continue;
            }
            if (!row.Checked && row.Total is { } printed)
                warnings.Add($"{item.Name}: quantidade × valor unitário não bate com o total do PDF " +
                             $"({row.Quantity:0.###} × {row.UnitPrice:0.00} ≠ {printed:0.00}). Confira.");
            items.Add(new Occurrence(item.Name, table.Title, new Material(row.Quantity, row.Unit, row.UnitPrice)));
        }
        if (items.Count > 0)
            tables.Add(new MaterialTable(table.Title, items));
    }

    /// <summary>
    /// A row of column names ("FERRAGENS UN QTD VALOR UNIT. VALOR TOTAL"). The words before the first
    /// column name are the table title; null when the row starts with a column name.
    /// </summary>
    private static bool TryHeader(string line, out string? title, out int firstColumn)
    {
        title = null;
        firstColumn = 0;
        if (line.Length > 120 || line.Any(char.IsDigit))
            return false;

        var words = line.ToUpperInvariant().Split(' ');
        var first = -1;
        bool unit = false, quantity = false, price = false;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i].Trim('.', ':', '(', ')');
            var isUnit = UnitWords.Contains(word);
            var isQuantity = QuantityWords.Contains(word);
            var isPrice = PriceWords.Contains(word);
            if (!isUnit && !isQuantity && !isPrice)
                continue;
            unit |= isUnit;
            quantity |= isQuantity;
            price |= isPrice;
            if (first < 0)
                first = i;
        }
        if ((unit ? 1 : 0) + (quantity ? 1 : 0) + (price ? 1 : 0) < 2)
            return false;

        firstColumn = words.Take(first).Sum(w => w.Length + 1);
        var before = Config.Normalize(string.Join(' ', words.Take(first)));
        title = before.Length > 0 ? before : null;
        return true;
    }

    private static string TitleFrom(string? text) =>
        text is { Length: > 0 and <= MaxTitleLength } && Config.Normalize(text) is { Length: > 0 } title
            ? title
            : MaterialTable.Untitled;

    private static Row? TryRow(string line, bool nameless = false)
    {
        if ((nameless ? ColumnsOnly() : ItemLine()).Match(line) is not { Success: true } m)
            return null;
        var name = m.Groups["name"].Value.Trim();
        if (name.Length > 0 && (!name.Any(char.IsLetter) || Config.IsTotalLabel(name)))
            return null;

        var unit = m.Groups["unit"].Value.TrimEnd('.');
        var hasPrice = m.Groups["price"].Success;
        var price = hasPrice ? Money(m.Groups["price"].Value) : 0;
        double? total = m.Groups["total"].Success ? Money(m.Groups["total"].Value) : null;
        var tail = m.Groups["unit"].Success ? Math.Min(m.Groups["unit"].Index, m.Groups["qty"].Index) : m.Groups["qty"].Index;

        Row With(double quantity, bool matches) => new(name, unit, quantity, price, total, hasPrice, matches, tail);

        // "1.613" is 1613 in a Brazilian document; "2.5" can only be two and a half.
        var text = m.Groups["qty"].Value;
        var quantity = ParseNumber(text, dotIsThousands: Thousands().IsMatch(text));
        if (total is null)
            return With(quantity, false);
        if (Matches(quantity, price, total.Value))
            return With(quantity, true);

        // The printed total settles a quantity that reads both ways ("1.500" as 1500 or 1.5).
        var other = ParseNumber(text, dotIsThousands: !Thousands().IsMatch(text));
        return Matches(other, price, total.Value) ? With(other, true) : With(quantity, false);
    }

    private static bool Matches(double quantity, double price, double total) =>
        Math.Abs(quantity * price - total) <= PriceTolerance + Math.Abs(total) * 1e-6;

    private static void CheckTotal(Table table, double expected, List<string> warnings)
    {
        var sum = table.Items.Sum(i => Math.Round(i.Row.Quantity * i.Row.UnitPrice, 2));
        if (Math.Abs(sum - expected) > PriceTolerance * Math.Max(1, table.Items.Count))
            warnings.Add($"O total de \"{table.Title}\" no PDF é {expected:0.00}, mas os itens lidos somam " +
                         $"{sum:0.00}. Algum material pode ter ficado de fora.");
    }

    /// <summary>
    /// The project code is the first four digits of the trailing "0000-00" of the document code
    /// ("TCE-LC-DVL-4310-00"), the date comes right below it and the city from the "Cidade:" row.
    /// </summary>
    private static ProjectInfo ReadProjectInfo(IReadOnlyList<TextLine> lines, string? fileName)
    {
        int? code = null;
        string? date = null;
        string? city = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (code is null && DocumentCode().Match(lines[i].Text) is { Success: true } match)
            {
                code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                date = lines.Skip(i).Take(8).Select(l => Date().Match(l.Text)).FirstOrDefault(d => d.Success)?.Value;
            }
            if (city is null && City().Match(lines[i].Text) is { Success: true } found)
                city = found.Groups[1].Value.Trim();
        }
        if (code is null && fileName is not null && DocumentCode().Match(fileName) is { Success: true } inName)
            code = int.Parse(inName.Groups[1].Value, CultureInfo.InvariantCulture);
        date ??= lines.Select(l => Date().Match(l.Text)).FirstOrDefault(d => d.Success)?.Value;
        return new ProjectInfo(code, city, date);
    }

    /// <summary>"R$ 1.234,56", "1.234,56" or the "R$ -" Excel prints for zero.</summary>
    private static double Money(string text)
    {
        var number = text.Replace("R$", string.Empty, StringComparison.Ordinal).Trim();
        return number is "-" or "" ? 0 : ParseNumber(number, dotIsThousands: true);
    }

    private static double ParseNumber(string text, bool dotIsThousands) =>
        double.Parse(dotIsThousands ? text.Replace(".", string.Empty).Replace(',', '.') : text.Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{3})+(?:,\d+)?$|,")]
    private static partial Regex Thousands();

    // Unit and quantity in either order (or no unit), then up to two money values.
    private const string Columns =
        @"(?:(?<unit>\p{L}[\p{L}²³º]{0,7}\.?)\s+(?<qty>\d[\d.]*(?:,\d+)?)" +
        @"|(?<qty>\d[\d.]*(?:,\d+)?)\s+(?<unit>\p{L}[\p{L}²³º]{0,7}\.?)" +
        @"|(?<qty>\d[\d.]*(?:,\d+)?))" +
        @"(?:\s+(?<price>R\$\s*-|(?:R\$\s*)?\d[\d.]*,\d{2})(?:\s+(?<total>R\$\s*-|(?:R\$\s*)?\d[\d.]*,\d{2}))?)?$";

    // The name is lazy and may itself end in digits ("ABRAÇADEIRA BAP 3").
    [GeneratedRegex(@"^(?<name>.+?)\s+" + Columns)]
    private static partial Regex ItemLine();

    // A tall cell prints its name on the lines around the one with the numbers.
    [GeneratedRegex(@"^(?<name>)" + Columns)]
    private static partial Regex ColumnsOnly();

    [GeneratedRegex(@"^(?:SUB[- ]?)?TOTAL(?:\s+\p{L}+){0,4}\s*:?\s*(?:R\$\s*)?(?<value>\d[\d.]*,\d{2}|-)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLine();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2,}(?:-[A-Za-z0-9]+)*-(\d{4})-\d{2}(?!\d)")]
    private static partial Regex DocumentCode();

    [GeneratedRegex(@"\b\d{2}/\d{2}/\d{4}\b")]
    private static partial Regex Date();

    [GeneratedRegex(@"^Cidade\s*:?\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex City();
}
