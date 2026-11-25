using ListaMateriais.Kmz;

namespace ListaMateriais.Planilhas;

/// <summary>
/// A condition on a column, named by its header so the same rule works on sheets whose columns are in
/// different places. Columns without a header are named "Coluna C".
/// </summary>
/// <param name="Or">Joins this condition to the previous one with OU instead of E.</param>
internal sealed record Condition(string Column, string Operator, string Value, bool Or = false, bool Active = true);

/// <summary>Cells to paint: which columns, which color, and in which rows.</summary>
/// <param name="Columns">Header names; empty paints the whole row (every column of the table).</param>
/// <param name="Fill">The color of the cells; null takes their fill away.</param>
/// <param name="When">Rows to paint; no condition means every row.</param>
/// <param name="IncludeHeader">Paints the header cells of the columns too.</param>
internal sealed record PaintRule(IReadOnlyList<string> Columns, Color? Fill, IReadOnlyList<Condition> When,
    bool IncludeHeader = false);

/// <summary>What to do to a workbook.</summary>
/// <param name="Sheets">Names of the sheets to change; the others are left as they are.</param>
/// <param name="Paint">Applied in order: where two rules paint the same cell, the last one wins.</param>
/// <param name="Filter">Rows that do not meet it are hidden; empty hides nothing.</param>
/// <param name="HeaderRow">Excel row number of the column names; 0 finds it on each sheet.</param>
internal sealed record Job(IReadOnlyList<string> Sheets, IReadOnlyList<PaintRule> Paint, IReadOnlyList<Condition> Filter,
    int HeaderRow = 0);

/// <summary>What the job does to one sheet.</summary>
/// <param name="HeaderRow">0 when the sheet has no data.</param>
/// <param name="Shown">Rows left visible by the filter; null when no filter was applied to the sheet.</param>
internal sealed record SheetResult(string Name, int HeaderRow, int DataRows, int PaintedCells, int? Shown, int Hidden,
    IReadOnlyList<string> Warnings)
{
    public bool Changes => PaintedCells > 0 || Shown is not null;
}

internal sealed record JobResult(IReadOnlyList<SheetResult> Sheets)
{
    public bool Changes => Sheets.Any(s => s.Changes);

    /// <summary>Every warning, each starting with the sheet it is about.</summary>
    public IReadOnlyList<string> Warnings =>
        [.. Sheets.SelectMany(s => s.Warnings.Select(w => $"Aba \"{s.Name}\": {w}"))];
}

/// <summary>One data sheet as read: the text of its cells by Excel row number.</summary>
/// <param name="Rows">Only the rows that hold some value.</param>
/// <param name="Problem">Why the sheet cannot be worked on; null when it can.</param>
/// <param name="Uncalculated">Columns with formulas whose result was never saved in the file.</param>
internal sealed record SheetData(string Name, string Path, SortedDictionary<int, string?[]> Rows, string? Problem = null,
    IReadOnlySet<int>? Uncalculated = null)
{
    private const int HeaderSearchRows = 20;

    /// <summary>
    /// The row of the column names: the first of the top rows that is nearly as wide as the widest of them,
    /// so a title above the table is passed over. 0 when the sheet is empty.
    /// </summary>
    public int FindHeaderRow()
    {
        var top = Rows.Take(HeaderSearchRows).Select(r => (Row: r.Key, Filled: r.Value.Count(c => c is not null))).ToList();
        if (top.Count == 0)
            return 0;
        var wide = Math.Max(1, (int)Math.Ceiling(top.Max(r => r.Filled) * 0.6));
        return top.First(r => r.Filled >= wide).Row;
    }

    /// <summary>The name of each column of the table, in order: its header, or "Coluna C" without one.</summary>
    /// <param name="headerRow">0 finds it.</param>
    public IReadOnlyList<(int Index, string Name)> Columns(int headerRow = 0)
    {
        if (headerRow <= 0)
            headerRow = FindHeaderRow();
        var data = Rows.Where(r => r.Key >= headerRow).Select(r => r.Value).ToList();
        if (headerRow == 0 || data.Count == 0)
            return [];
        var header = Rows.GetValueOrDefault(headerRow) ?? [];
        var width = data.Max(r => r.Length);
        var columns = new List<(int, string)>();
        for (var i = 0; i < width; i++)
        {
            if (i < header.Length && header[i] is { } name)
                columns.Add((i, name));
            else if (data.Any(r => i < r.Length && r[i] is not null))
                columns.Add((i, LetterName(i)));
        }
        return columns;
    }

    public static string LetterName(int index) => $"Coluna {SheetTable.ColumnLetter(index)}";

    /// <summary>
    /// The columns a name stands for: every header equal to it (ignoring case, accents and spaces around),
    /// or the column of the letter in "Coluna C".
    /// </summary>
    public static IReadOnlyList<int> Find(IReadOnlyList<(int Index, string Name)> columns, string name)
    {
        var wanted = Key(name);
        return [.. columns.Where(c => Key(c.Name) == wanted || Key(LetterName(c.Index)) == wanted).Select(c => c.Index)];
    }

    /// <summary>Two column names are the same column when their keys are equal.</summary>
    public static string Key(string name) =>
        string.Join(' ', SheetFilters.Normalize(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
