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

    public static readonly string SettingsFolder = Path.Combine(
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

    /// <summary>Categories of the "Lista de materiais" in the Route Map PDF report.</summary>
    public static readonly IReadOnlyList<string> DefaultPdfTableTitles =
    [
        "FERRAGENS",
        "CABOS / LANÇAMENTO",
        "FUSÃO",
        "DATA CENTER — PASSIVO",
        "DATA CENTER — ATIVO",
    ];

    public static IReadOnlyList<string> DefaultTitles(bool pdf) => pdf ? DefaultPdfTableTitles : DefaultTableTitles;

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

    /// <summary>A saved value, or <paramref name="fallback"/> when it is missing or of another type.</summary>
    private static T Read<T>(string key, T fallback)
    {
        try
        {
            return ReadSettings()[key] is { } node ? node.GetValue<T>() : fallback;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static TEnum ReadEnum<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Read<string?>(key, null), out var value) && Enum.IsDefined(value) ? value : fallback;

    private static void Write(string key, JsonNode? value)
    {
        var json = ReadSettings();
        json[key] = value;
        WriteSettings(json);
    }

    /// <summary>
    /// Runs a Save* method. A setting that cannot be written (read-only profile, disk full) is not worth
    /// stopping the user for: the failure is logged and its message returned, null when it worked.
    /// </summary>
    public static string? TrySave(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível salvar as configurações: {0}", e.Message);
            return e.Message;
        }
    }

    private static string SettingsKey(bool pdf) => pdf ? "pdfHeaders" : "headers";

    /// <summary>Table headers to look for, normalized; defaults until the user saves a list.</summary>
    public static IReadOnlyList<string> LoadTableTitles(bool pdf = false)
    {
        try
        {
            var settings = ReadSettings();
            if (settings[SettingsKey(pdf)] is not JsonArray saved)
                return DefaultTitles(pdf);
            return saved
                .Select(n => n?.GetValue<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => Normalize(s!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return DefaultTitles(pdf);
        }
    }

    public static void SaveTableTitles(IEnumerable<string> titles, bool pdf = false)
    {
        var json = ReadSettings();
        json[SettingsKey(pdf)] = new JsonArray(titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Normalize(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (JsonNode)JsonValue.Create(t)!)
            .ToArray());
        WriteSettings(json);
    }

    public static string? LoadTemplatePath()
    {
        foreach (var candidate in new[] { Read<string?>("template", null), Template })
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

    /// <summary>
    /// True when there is a default template: built into this executable, or kept from an earlier one
    /// (the versions downloaded by the updater come without the client's templates).
    /// </summary>
    public static bool HasBuiltInTemplate =>
        typeof(Config).Assembly.GetManifestResourceInfo(BuiltInTemplateResource) is not null || File.Exists(BuiltInTemplateFile);

    /// <summary>
    /// Copies the templates built into this executable to the settings folder, so they stay available
    /// after an update replaces it with a version built without them. The spreadsheet is copied only
    /// when missing (the user may have edited it); the Word templates are kept up to date.
    /// </summary>
    public static void KeepBuiltInTemplates()
    {
        try
        {
            if (!File.Exists(BuiltInTemplateFile))
                UnpackBuiltInTemplate();
            foreach (var (resourceName, kept) in new[]
                     {
                         (WordTemplate(ReportKind.Tcr).Resource, KeptWordTemplate(ReportKind.Tcr)),
                         (WordTemplate(ReportKind.Tce).Resource, KeptWordTemplate(ReportKind.Tce)),
                         (PresentationResource, KeptPresentationTemplate),
                     })
            {
                using var resource = typeof(Config).Assembly.GetManifestResourceStream(resourceName);
                if (resource is null)
                    continue;
                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                if (File.Exists(kept) && buffer.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(kept)))
                    continue;
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllBytes(kept + ".tmp", buffer.ToArray());
                File.Move(kept + ".tmp", kept, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível guardar os modelos padrão: {0}", e.Message);
        }
    }

    private const string PresentationResource = "ListaMateriais.apresentacao.pptx";

    /// <summary>Where the presentation template built into the executable is kept for the versions without it.</summary>
    private static string KeptPresentationTemplate => Path.Combine(SettingsFolder, "modelo-apresentacao.pptx");

    /// <summary>
    /// The look of the presentations: "apresentacao.pptx" next to the executable, else the one built in (or kept from
    /// an earlier copy). Null when there is none: the presentations then use a plain look.
    /// </summary>
    public static byte[]? LoadPresentationTemplate()
    {
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "apresentacao.pptx");
            if (File.Exists(beside))
                return Files.ReadAllBytesShared(beside);
            using var resource = typeof(Config).Assembly.GetManifestResourceStream(PresentationResource);
            if (resource is not null)
            {
                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                return buffer.ToArray();
            }
            return File.Exists(KeptPresentationTemplate) ? File.ReadAllBytes(KeptPresentationTemplate) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Não foi possível ler o modelo de apresentação: {0}", e.Message);
            return null;
        }
    }

    /// <summary>Where the Word template built into the executable is kept for the versions without it.</summary>
    private static string KeptWordTemplate(ReportKind kind) =>
        Path.Combine(SettingsFolder, $"modelo-padrao-{kind.ToString().ToLowerInvariant()}.docx");

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

    public static void SaveTemplatePath(string path) => Write("template", Path.GetFullPath(path));

    public static readonly string TcrTemplate = Path.Combine(AppContext.BaseDirectory, "templates", "tcr.docx");
    public static readonly string TceTemplate = Path.Combine(AppContext.BaseDirectory, "templates", "tce.docx");

    private static (string Key, string File, string Resource) WordTemplate(ReportKind kind) => kind == ReportKind.Tce
        ? ("tceTemplate", TceTemplate, "ListaMateriais.tce.docx")
        : ("tcrTemplate", TcrTemplate, "ListaMateriais.tcr.docx");

    /// <summary>The Word template file in use (picked by the user, or next to the executable); null when there is none.</summary>
    public static string? WordTemplatePath(ReportKind kind)
    {
        var (key, file, _) = WordTemplate(kind);
        return new[] { Read<string?>(key, null), file }.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public static bool HasBuiltInWordTemplate(ReportKind kind) =>
        typeof(Config).Assembly.GetManifestResourceInfo(WordTemplate(kind).Resource) is not null
        || File.Exists(KeptWordTemplate(kind));

    /// <summary>
    /// The Word template of a report: the one picked by the user, the one next to the executable, or the
    /// one built into it. Null when there is none.
    /// </summary>
    public static byte[]? LoadWordTemplate(ReportKind kind)
    {
        if (WordTemplatePath(kind) is { } path)
            return Files.ReadAllBytesShared(path);

        using var stream = typeof(Config).Assembly.GetManifestResourceStream(WordTemplate(kind).Resource);
        if (stream is null)
            return File.Exists(KeptWordTemplate(kind)) ? Files.ReadAllBytesShared(KeptWordTemplate(kind)) : null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <param name="path">Null goes back to the template next to the executable or built into it.</param>
    public static void SaveWordTemplatePath(ReportKind kind, string? path) =>
        Write(WordTemplate(kind).Key, path is null ? null : Path.GetFullPath(path));

    /// <summary>What to generate from a PDF report, one or more files; Word (TCR) until the user chooses otherwise.</summary>
    public static PdfOutput LoadPdfOutput() =>
        Enum.TryParse<PdfOutput>(Read<string?>("pdfOutput", null), out var value) && value != PdfOutput.None
                                                                                 && (value & ~PdfOutput.All) == 0
            ? value
            : PdfOutput.Word;

    public static void SavePdfOutput(PdfOutput output) => Write("pdfOutput", output.ToString());

    /// <summary>
    /// Whether the highlighted spots of the Word template that the app could not fill are asked for
    /// when generating (true) or left highlighted to be typed in Word (false).
    /// </summary>
    public static bool LoadAskPlaceholders() => Read("askPlaceholders", true);

    public static void SaveAskPlaceholders(bool ask) => Write("askPlaceholders", ask);

    public static OutputLayout LoadOutputLayout() => ReadEnum("layout", OutputLayout.Simplified);

    public static void SaveOutputLayout(OutputLayout layout) => Write("layout", layout.ToString());

    /// <summary>Compact layout: also hide the title and TOTAL of sections with no material.</summary>
    public static bool LoadHideEmptySections() => Read("hideEmptySections", false);

    public static void SaveHideEmptySections(bool hide) => Write("hideEmptySections", hide);

    /// <summary>How the placemarks of the last KMZ looked, to start the next one the same way.</summary>
    public static KmzOptions LoadKmzOptions()
    {
        var defaults = new KmzOptions();
        try
        {
            if (ReadSettings()["kmz"] is not JsonObject saved)
                return defaults;
            return new KmzOptions(
                Enum.TryParse<Kmz.MarkerShape>(saved["shape"]?.GetValue<string>(), out var shape) && Enum.IsDefined(shape)
                    ? shape : defaults.Shape,
                saved["color"]?.GetValue<int>() ?? defaults.Color,
                saved["size"]?.GetValue<int>() is { } size and >= 0 and <= 2 ? size : defaults.Size,
                saved["longitudeFirst"]?.GetValue<bool>() ?? defaults.LongitudeFirst);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return defaults;
        }
    }

    public static void SaveKmzOptions(KmzOptions options) => Write("kmz", new JsonObject
    {
        ["shape"] = options.Shape.ToString(),
        ["color"] = options.Color,
        ["size"] = options.Size,
        ["longitudeFirst"] = options.LongitudeFirst,
    });

    /// <summary>How the last simple map was made: only the cables (true) or every group; color name; line width.</summary>
    public static (bool CablesOnly, string Color, int Width) LoadSimplifyOptions() =>
        (Read("simplifyCablesOnly", true), Read("simplifyCableColor", Kmz.MapColors.NeonName), Read("simplifyWidth", 10));

    public static void SaveSimplifyOptions(bool cablesOnly, string color, int width)
    {
        Write("simplifyCablesOnly", cablesOnly);
        Write("simplifyCableColor", color);
        Write("simplifyWidth", width);
    }

    /// <summary>How the last DWG was placed on the map: in degrees or UTM, zone, south, unit (0 m, 1 cm, 2 mm), and whether blocks are drawn.</summary>
    public static (bool Degrees, int Zone, bool South, int Unit, bool DrawBlocks) LoadDwgOptions() =>
        (Read("dwgDegrees", false), Math.Clamp(Read("dwgZone", 23), 1, 60), Read("dwgSouth", true),
            Math.Clamp(Read("dwgUnit", 0), 0, 2), Read("dwgDrawBlocks", false));

    public static void SaveDwgOptions(bool degrees, int zone, bool south, int unit, bool drawBlocks)
    {
        Write("dwgDegrees", degrees);
        Write("dwgZone", zone);
        Write("dwgSouth", south);
        Write("dwgUnit", unit);
        Write("dwgDrawBlocks", drawBlocks);
    }

    /// <summary>How the last map check was made: join distance and longest span, in meters, and whether spans are checked.</summary>
    public static (int JoinDistance, bool CheckSpans, int MaxSpan) LoadCheckOptions() =>
        (Math.Clamp(Read("checkJoinDistance", 10), 1, 100), Read("checkSpans", false), Math.Clamp(Read("checkMaxSpan", 80), 10, 1000));

    public static void SaveCheckOptions(int joinDistance, bool checkSpans, int maxSpan)
    {
        Write("checkJoinDistance", joinDistance);
        Write("checkSpans", checkSpans);
        Write("checkMaxSpan", maxSpan);
    }

    /// <summary>Whether the app looks for a new version when it opens; on until the user turns it off.</summary>
    public static bool LoadCheckUpdates() => Read("checkUpdates", true);

    public static void SaveCheckUpdates(bool check) => Write("checkUpdates", check);

    /// <summary>Where the window was when the app closed: left, top, width, height and whether it was maximized.</summary>
    public static (int Left, int Top, int Width, int Height, bool Maximized)? LoadWindow()
    {
        try
        {
            return ReadSettings()["window"] is JsonArray { Count: 5 } w && w.All(n => n is not null)
                ? (w[0]!.GetValue<int>(), w[1]!.GetValue<int>(), w[2]!.GetValue<int>(), w[3]!.GetValue<int>(), w[4]!.GetValue<bool>())
                : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static void SaveWindow(int left, int top, int width, int height, bool maximized) =>
        Write("window", new JsonArray(left, top, width, height, maximized));
}

/// <param name="Color">RGB of the marker, as 0xRRGGBB.</param>
/// <param name="Size">0 small, 1 medium, 2 large.</param>
internal sealed record KmzOptions(Kmz.MarkerShape Shape = Kmz.MarkerShape.Pin, int Color = 0xE53935, int Size = 1,
    bool LongitudeFirst = false);

/// <summary>The files generated from a PDF report; several can be chosen at once.</summary>
[Flags]
internal enum PdfOutput
{
    None = 0,

    /// <summary>The TCR Word document.</summary>
    Word = 1,

    /// <summary>The TCE Word document.</summary>
    Tce = 2,

    /// <summary>The material list spreadsheet (.xlsx).</summary>
    Spreadsheet = 4,

    All = Word | Tce | Spreadsheet,
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
