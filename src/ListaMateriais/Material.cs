namespace ListaMateriais;

internal sealed record Material(double Quantity, string Unit, double UnitPrice);

/// <summary>Project data read from the Word document; null when not found.</summary>
internal sealed record ProjectInfo(int? Code, string? City, string? Date);

/// <summary>One row of a material table in the document.</summary>
internal sealed record Occurrence(string Name, string Table, Material Material);

/// <summary>A table of the document that lists materials, with its rows in order.</summary>
/// <param name="Title">Normalized title (first cell), or <see cref="MaterialTable.Untitled"/>.</param>
internal sealed record MaterialTable(string Title, IReadOnlyList<Occurrence> Items)
{
    public const string Untitled = "(SEM TÍTULO)";
}

/// <param name="Tables">Every table that looks like a material list, in document order.</param>
/// <param name="EmbeddedWorkbooks">How many Excel workbooks are embedded in the document.</param>
internal sealed record DocxContent(
    IReadOnlyList<MaterialTable> Tables,
    ProjectInfo Info,
    int EmbeddedWorkbooks,
    IReadOnlyList<string> Warnings);

/// <summary>Combines the rows of the chosen tables into one entry per material.</summary>
internal static class MaterialGroups
{
    /// <summary>
    /// Rows grouped by the name looked up in the template, so aliases and case variants
    /// count as the same material. Groups keep document order.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Occurrence>> Group(IEnumerable<MaterialTable> tables)
    {
        var groups = new OrderedDictionary<string, List<Occurrence>>(StringComparer.Ordinal);
        foreach (var item in tables.SelectMany(t => t.Items))
        {
            var key = Config.LookupName(item.Name);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = [];
            group.Add(item);
        }
        return groups.Values.ToList();
    }

    /// <summary>
    /// One entry per material, named after its first row. Repeated quantities are added up
    /// when <paramref name="sum"/> is true, otherwise only the first row counts.
    /// </summary>
    public static OrderedDictionary<string, Material> Combine(
        IReadOnlyList<IReadOnlyList<Occurrence>> groups, bool sum)
    {
        var materials = new OrderedDictionary<string, Material>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var first = group[0];
            var quantity = sum ? group.Sum(o => o.Material.Quantity) : first.Material.Quantity;
            var unit = group.Select(o => o.Material.Unit).FirstOrDefault(u => u.Length > 0) ?? string.Empty;
            var price = group.Select(o => o.Material.UnitPrice).FirstOrDefault(p => p != 0);
            materials[first.Name] = new Material(quantity, unit, price);
        }
        return materials;
    }
}
