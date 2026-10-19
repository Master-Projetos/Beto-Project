using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ListaMateriais;

internal static partial class Config
{
    public const string SheetName = "MATERIAIS";
    public const int HeaderRow = 8;
    public const int FirstItemRow = 10;
    public const string GrandTotalCell = "G8";
    public const string SectionTitleCell = "A9";
    public const string ProjectCodeCell = "B4";
    public const string DateCell = "D4";
    public const string CityCell = "D6";

    public static readonly string Template =
        Path.Combine(AppContext.BaseDirectory, "templates", "lista_materiais.xlsx");

    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ListaMateriais");

    public static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    /// <summary>Where the template built into the executable is unpacked so it can be opened and edited.</summary>
    public static readonly string BuiltInTemplateFile = Path.Combine(SettingsFolder, "modelo-padrao.xlsx");

    private const string BuiltInTemplateResource = "ListaMateriais.lista_materiais.xlsx";

    public static readonly IReadOnlyList<string> DefaultTableTitles =
    [
        "MATERIAL",
        "MATERIAIS",
        "INSTALAÇÃO CLIENTE",
        "FERRAGENS",
        "LANÇAMENTO DE CABOS",
        "CAIXAS / SPLITTERS",
        "DATA CENTER E POP - INDOOR (EQUIPAMENTOS PASSIVOS)",
    ];

    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.Ordinal)
    {
        ["CONECTOR RJ45"] = "CONECTORES RJ45",
        ["DROP"] = "DROP FLAT 1FO",
    };

    /// <summary>Upper case, trimmed, with runs of whitespace collapsed to one space.</summary>
    public static string Normalize(string description) =>
        Whitespace().Replace(description, " ").Trim().ToUpperInvariant();

    public static string LookupName(string materialName)
    {
        var name = Normalize(materialName);
        return Normalize(NameAliases.GetValueOrDefault(name, name));
    }

    /// <summary>"Total", "TOTAL:", "Total geral", "Subtotal"... in any case.</summary>
    public static bool IsTotalLabel(string text) => TotalLabel().IsMatch(Normalize(text));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(SUB[- ]?)?TOTAL( GERAL)?\s*:?$")]
    private static partial Regex TotalLabel();

    public static IReadOnlyList<(string CellRef, string Formula)> GrandTotal(int totalRow)
    {
        var total = totalRow > FirstItemRow ? $"SUM(H{FirstItemRow}:H{totalRow - 1})" : "0";
        return [($"H{totalRow}", total), (GrandTotalCell, $"H{totalRow}")];
    }

    private static JsonObject ReadSettings()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException
                                      or FormatException)
        {
            return new JsonObject();
        }
    }

    private static void WriteSettings(JsonObject json)
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsFile, json.ToJsonString());
    }

    /// <summary>Table headers to look for, normalized; defaults until the user saves a list.</summary>
    public static IReadOnlyList<string> LoadTableTitles()
    {
        try
        {
            var settings = ReadSettings();
            if (settings["headers"] is not JsonArray saved)
                return DefaultTableTitles;
            return saved
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => Normalize(s!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return DefaultTableTitles;
        }
    }

    public static void SaveTableTitles(IEnumerable<string> titles)
    {
        var json = ReadSettings();
        json["headers"] = new JsonArray(titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Normalize(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (JsonNode)JsonValue.Create(t)!)
            .ToArray());
        WriteSettings(json);
    }

    public static string? LoadTemplatePath()
    {
        string? saved = null;
        try
        {
            saved = ReadSettings()["template"]?.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            saved = null;
        }

        foreach (var candidate in new[] { saved, Template })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        try
        {
            return File.Exists(BuiltInTemplateFile) ? BuiltInTemplateFile : UnpackBuiltInTemplate();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível extrair o modelo padrão: {0}", e.Message);
            return null;
        }
    }

    public static bool HasBuiltInTemplate =>
        typeof(Config).Assembly.GetManifestResourceInfo(BuiltInTemplateResource) is not null;

    public static bool IsBuiltInTemplate(string path) =>
        string.Equals(Path.GetFullPath(path), BuiltInTemplateFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the unpacked built-in template was changed since it was unpacked.</summary>
    public static bool BuiltInTemplateWasEdited()
    {
        if (!File.Exists(BuiltInTemplateFile))
            return false;
        using var resource = typeof(Config).Assembly.GetManifestResourceStream(BuiltInTemplateResource);
        if (resource is null)
            return false;
        using var original = new MemoryStream();
        resource.CopyTo(original);
        return !original.ToArray().AsSpan().SequenceEqual(Files.ReadAllBytesShared(BuiltInTemplateFile));
    }

    /// <summary>
    /// Writes the template built into the executable to <see cref="BuiltInTemplateFile"/>,
    /// replacing it. Null when the executable was built without one.
    /// </summary>
    public static string? UnpackBuiltInTemplate()
    {
        using var resource = typeof(Config).Assembly.GetManifestResourceStream(BuiltInTemplateResource);
        if (resource is null)
            return null;
        Directory.CreateDirectory(SettingsFolder);
        var temp = BuiltInTemplateFile + ".tmp";
        using (var file = File.Create(temp))
            resource.CopyTo(file);
        File.Move(temp, BuiltInTemplateFile, overwrite: true);
        return BuiltInTemplateFile;
    }

    public static void SaveTemplatePath(string path)
    {
        var json = ReadSettings();
        json["template"] = Path.GetFullPath(path);
        WriteSettings(json);
    }

    public static OutputLayout LoadOutputLayout()
    {
        try
        {
            return Enum.TryParse<OutputLayout>(ReadSettings()["layout"]?.GetValue<string>(), out var layout)
                   && Enum.IsDefined(layout)
                ? layout
                : OutputLayout.Simplified;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return OutputLayout.Simplified;
        }
    }

    public static void SaveOutputLayout(OutputLayout layout)
    {
        var json = ReadSettings();
        json["layout"] = layout.ToString();
        WriteSettings(json);
    }

    /// <summary>Compact layout: also hide the title and TOTAL of sections with no material.</summary>
    public static bool LoadHideEmptySections()
    {
        try
        {
            return ReadSettings()["hideEmptySections"]?.GetValue<bool>() ?? false;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static void SaveHideEmptySections(bool hide)
    {
        var json = ReadSettings();
        json["hideEmptySections"] = hide;
        WriteSettings(json);
    }
}

internal enum OutputLayout
{
    /// <summary>Only the materials found in the document, one section.</summary>
    Simplified,

    /// <summary>The whole template list, sections and subtotals kept; unused materials get 0.</summary>
    Complete,

    /// <summary>Like <see cref="Complete"/>, but the materials not in the document are hidden.</summary>
    Compact,
}
