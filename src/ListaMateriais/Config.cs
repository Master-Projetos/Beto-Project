using System.Text.Json;
using System.Text.Json.Nodes;

namespace ListaMateriais;

internal static class Config
{
    public const string SheetName = "MATERIAIS";
    public const int FirstItemRow = 10;
    public const string GrandTotalCell = "G8";
    public const string SectionTitleCell = "A9";

    public static readonly string Template =
        Path.Combine(AppContext.BaseDirectory, "templates", "lista_materiais.xlsx");

    public static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ListaMateriais",
        "settings.json");

    public static readonly IReadOnlySet<string> MaterialTableTitles =
        new HashSet<string>(StringComparer.Ordinal) { "MATERIAL", "MATERIAIS", "INSTALAÇÃO CLIENTE" };

    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.Ordinal)
    {
        ["CONECTOR RJ45"] = "CONECTORES RJ45",
        ["DROP"] = "DROP FLAT 1FO",
    };

    public static string Normalize(string description) => description.Trim().ToUpperInvariant();

    public static string LookupName(string materialName) =>
        Normalize(NameAliases.GetValueOrDefault(materialName, materialName));

    public static IReadOnlyList<(string CellRef, string Formula)> GrandTotal(int totalRow)
    {
        var total = totalRow > FirstItemRow ? $"SUM(H{FirstItemRow}:H{totalRow - 1})" : "0";
        return [($"H{totalRow}", total), (GrandTotalCell, $"H{totalRow}")];
    }

    public static string? LoadTemplatePath()
    {
        string? saved = null;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(SettingsFile));
            saved = json?["template"]?.GetValue<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException
                                      or FormatException)
        {
            saved = null;
        }

        foreach (var candidate in new[] { saved, Template })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static void SaveTemplatePath(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var json = new JsonObject { ["template"] = Path.GetFullPath(path) };
        File.WriteAllText(SettingsFile, json.ToJsonString());
    }
}
