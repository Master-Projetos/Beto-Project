using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Core;

namespace ListaMateriais;

/// <summary>
/// Reads the "Lista de materiais" of the project report exported by Route Map (PDF). Each category
/// (Ferragens, Cabos / Lançamento, Fusão...) starts with a "Material Qtd. Un. Valor unit. Total"
/// header and ends with its "Subtotal" line.
/// </summary>
internal static partial class PdfReader
{
    private const double PriceTolerance = 0.02;

    /// <summary>
    /// Reads the material tables of a PDF: the categories of the Route Map report or, when the PDF is
    /// a TCE/TCR saved from Word, its material tables (<see cref="DocumentContent.Report"/> is null then).
    /// </summary>
    public static DocumentContent Read(string pdfPath)
    {
        var name = Path.GetFileName(pdfPath);
        try
        {
            using var pdf = PdfDocument.Open(Files.ReadAllBytesShared(pdfPath));
            var pages = pdf.GetPages().ToList();
            // Content order keeps a wrapped material name together with its quantity and prices.
            var lines = pages
                .SelectMany(page => ContentOrderTextExtractor.GetText(page).Split('\n'))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            var content = Parse(lines);
            if (content.Tables.Count > 0)
                return content with { Report = content.Report! with { Map = ReadMap(pdf) } };

            // Not the Route Map report: a TCE/TCR saved from Word. Its tables are rebuilt from where the
            // words sit on the page; whichever reading finds more materials wins.
            var titles = Config.LoadTableTitles();
            var byPosition = TcePdfParser.Parse(LinesByPosition(pages), name, titles);
            var byContent = TcePdfParser.Parse(lines, name, titles);
            var best = Items(byContent) > Items(byPosition) ? byContent : byPosition;
            if (best.Tables.Count > 0)
                return best;

            var hasPictures = pages.Any(page => page.GetImages().Any());
            return best with
            {
                Warnings = lines.Count < 5
                    ? ["O PDF não tem texto selecionável (parece digitalizado ou salvo como imagem)."]
                    : hasPictures
                        ? ["As tabelas deste PDF podem estar como imagem, sem texto para ler."]
                        : [],
            };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AppException($"O documento \"{name}\" não foi encontrado.\n\n" +
                                   "Ele pode ter sido movido, renomeado ou excluído. Selecione-o novamente.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new AppException($"Sem permissão para ler o documento \"{name}\".", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível abrir o documento \"{name}\".\n\n" +
                                   "Se ele estiver aberto em outro programa, feche-o e tente de novo.", e);
        }
        catch (PdfDocumentFormatException e)
        {
            throw new AppException($"\"{name}\" não é um PDF válido ou está corrompido.", e);
        }
    }

    private static int Items(DocumentContent content) => content.Tables.Sum(t => t.Items.Count);

    /// <summary>The words of every page grouped into lines by their baseline, each line left to right.</summary>
    private static List<TextLine> LinesByPosition(IReadOnlyList<UglyToad.PdfPig.Content.Page> pages)
    {
        var lines = new List<TextLine>();
        for (var number = 0; number < pages.Count; number++)
        {
            var words = pages[number].GetWords()
                .Where(w => w.Letters.Count > 0 && !string.IsNullOrWhiteSpace(w.Text))
                .OrderByDescending(Baseline)
                .ToList();

            var line = new List<UglyToad.PdfPig.Content.Word>();
            foreach (var word in words)
            {
                // Words of one line share the baseline; a centered cell next to a two-line name sits half a line off.
                if (line.Count > 0 && Baseline(line[0]) - Baseline(word) > Math.Max(1.5, 0.3 * Size(word)))
                {
                    lines.Add(Line(line, number));
                    line.Clear();
                }
                line.Add(word);
            }
            if (line.Count > 0)
                lines.Add(Line(line, number));
        }
        return lines;

        static double Baseline(UglyToad.PdfPig.Content.Word word) => word.Letters[0].StartBaseLine.Y;

        static double Size(UglyToad.PdfPig.Content.Word word) => word.Letters[0].PointSize;

        static TextLine Line(List<UglyToad.PdfPig.Content.Word> words, int page)
        {
            var text = new System.Text.StringBuilder();
            var positions = new List<TextWord>();
            foreach (var word in words.OrderBy(w => w.BoundingBox.Left))
            {
                var clean = string.Concat(word.Text.Where(c => !char.IsWhiteSpace(c)));
                if (clean.Length == 0)
                    continue;
                if (text.Length > 0)
                    text.Append(' ');
                positions.Add(new TextWord(text.Length, word.BoundingBox.Left, word.BoundingBox.Right));
                text.Append(clean);
            }
            return new TextLine(text.ToString(), page, Baseline(words[0]), words.Max(Size), positions);
        }
    }

    /// <summary>Parses the text lines of the whole Route Map report, in reading order.</summary>
    internal static DocumentContent Parse(IReadOnlyList<string> lines)
    {
        var tables = new List<MaterialTable>();
        var warnings = new List<string>();

        string? title = null;
        List<Occurrence>? items = null;
        var nameStart = new List<string>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsPageDecoration(line))
                continue;

            if (TableHeader().IsMatch(line))
            {
                title = i > 0 ? Config.Normalize(lines[i - 1]) : MaterialTable.Untitled;
                items = [];
                nameStart.Clear();
                continue;
            }
            if (items is null)
                continue;

            if (SubtotalLine().Match(line) is { Success: true } subtotal)
            {
                CheckSubtotal(title!, items, ParseNumber(subtotal.Groups[1].Value), warnings);
                if (items.Count > 0)
                    tables.Add(new MaterialTable(title!, items));
                title = null;
                items = null;
                continue;
            }

            if (ItemLine().Match(line) is not { Success: true } m)
            {
                // A long name wraps: its first lines come before the line with the numbers.
                nameStart.Add(line);
                continue;
            }

            var name = string.Join(' ', nameStart.Append(m.Groups["name"].Value)).Trim();
            nameStart.Clear();
            var quantity = ParseNumber(m.Groups["qty"].Value);
            var unitPrice = ParseNumber(m.Groups["price"].Value);
            var total = ParseNumber(m.Groups["total"].Value);
            if (name.Length == 0)
            {
                warnings.Add($"Linha sem nome de material ignorada em {title}: \"{line}\".");
                continue;
            }
            if (Math.Abs(quantity * unitPrice - total) > PriceTolerance + total * 1e-6)
                warnings.Add($"{name}: quantidade × valor unitário não bate com o total do PDF " +
                             $"({quantity:0.###} × {unitPrice:0.00} ≠ {total:0.00}). Confira.");

            Trace.TraceInformation("{0} | {1} | unidade: {2} | quantidade: {3} | valor unitário: {4} | valor total: {5}",
                title, name, m.Groups["unit"].Value, quantity, unitPrice, total);
            items.Add(new Occurrence(name, title!, new Material(quantity, m.Groups["unit"].Value, unitPrice)));
        }

        if (items is { Count: > 0 })
        {
            // The report ended before the category's Subtotal line.
            warnings.Add($"A tabela \"{title}\" terminou sem a linha de subtotal; confira se o PDF está completo.");
            tables.Add(new MaterialTable(title!, items));
        }

        var info = ReadProjectInfo(lines);
        return new DocumentContent(tables, info, 0, warnings, ReadReport(lines, info));
    }

    /// <summary>Author, area study, scope and cost summary from the pages around the material lists.</summary>
    private static ProjectReport ReadReport(IReadOnlyList<string> lines, ProjectInfo info)
    {
        var text = string.Join('\n', lines);

        string? Text(string pattern) =>
            Regex.Match(text, pattern, RegexOptions.Multiline) is { Success: true } m ? Clean(m.Groups[1].Value) : null;

        double? Number(string pattern) => Text(pattern) is { } value ? ParseNumber(value) : null;

        string? Paragraph(string start, string end)
        {
            var from = lines.ToList().FindIndex(l => l.StartsWith(start, StringComparison.Ordinal));
            if (from < 0)
                return null;
            var body = lines.Skip(from).TakeWhile((l, i) => i == 0 || !l.StartsWith(end, StringComparison.Ordinal));
            return string.Join(' ', body)[start.Length..].Trim();
        }

        var generated = lines.ToList().FindIndex(l => GeneratedOn().IsMatch(l));
        return new ProjectReport(
            Title: generated >= 0 && generated + 1 < lines.Count ? lines[generated + 1] : null,
            City: Clean(info.City),
            Neighborhood: Text(@"^Bairro\s+(.+?)\s+Casas \(HP\)"),
            Author: Text(@"Elaborado por\s+(.+)$"),
            Date: info.Date,
            Objective: Paragraph("Objetivo:", "Escopo:") is { } objective ? DropEmptyNeighborhood(objective) : null,
            Scope: Paragraph("Escopo:", "Investimento estimado"),
            Houses: Number(@"Casas \(HP\)\s+([\d.,]+)"),
            ExistingPorts: Number(@"Portas existentes \(HC\)\s+([\d.,]+)"),
            NewPorts: Number(@"Novas portas\s+([\d.,]+)"),
            PenetrationRate: Text(@"Taxa de penetração\s+([\d.,]+%)"),
            LaunchMeters: Number(@"Lançamento estimado\s+([\d.,]+)\s*m"),
            Posts: Number(@"Postes \(estimativa\)\s+([\d.,]+)"),
            RegionalLabor: Number(@"M\.O\. regional\s+R\$\s*([\d.,]+)"),
            OutsourcedLabor: Number(@"M\.O\. terceirizada\s+R\$\s*([\d.,]+)"),
            SafetyPercent: Number(@"Coef\. de segurança \(([\d.,]+)%\)"),
            Days: Number(@"Prazo da obra\s+([\d.,]+)\s+dias"),
            Client: ReadClient(lines));
    }

    /// <summary>The route map of the report: the largest picture of the pages, as JPEG. Null when there is none.</summary>
    private static byte[]? ReadMap(PdfDocument pdf)
    {
        try
        {
            var best = pdf.GetPages().SelectMany(page => page.GetImages())
                .Where(i => i.WidthInSamples >= 400 && i.HeightInSamples >= 250)
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();
            if (best is null)
                return null;

            byte[]? source = best.RawBytes.Length > 0 ? best.RawBytes.ToArray() : null;
            if (!TryJpeg(source, out var jpeg) && best.TryGetPng(out var png))
                TryJpeg(png, out jpeg);
            return jpeg;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or IOException)
        {
            Trace.TraceWarning("Não foi possível ler a imagem do mapa do PDF: {0}", e.Message);
            return null;
        }
    }

    private static bool TryJpeg(byte[]? data, out byte[]? jpeg)
    {
        jpeg = null;
        if (data is null || data.Length == 0)
            return false;
        try
        {
            using var image = System.Drawing.Image.FromStream(new MemoryStream(data));
            using var output = new MemoryStream();
            image.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
            jpeg = output.ToArray();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The "CLIENTES B2B" block: name, then Endereço / Coordenadas / Atendimento / Serviço / Banda / IP / Vendedor.</summary>
    private static ClientInfo? ReadClient(IReadOnlyList<string> lines)
    {
        var start = lines.ToList().FindIndex(l => l.StartsWith("CLIENTES B2B", StringComparison.Ordinal));
        if (start < 0 || start + 1 >= lines.Count)
            return null;
        var block = lines.Skip(start + 2).TakeWhile(l => !l.StartsWith("MATERIAIS", StringComparison.Ordinal)).ToList();
        var text = string.Join(' ', block);

        string? Field(string label, string next) =>
            Regex.Match(text, label + @"\s*(.+?)\s*(?:" + next + ")", RegexOptions.Singleline) is { Success: true } m
                ? m.Groups[1].Value.Trim()
                : null;

        return new ClientInfo(
            Name: lines[start + 1],
            Address: Field("Endere[çc]o", @"\bCoordenadas\b"),
            Coordinates: Field("Coordenadas", @"\bAtendimento\b"),
            Network: Field("Atendimento", @"\bServi[çc]o\b"),
            Service: Field("Servi[çc]o", @"\bBanda\b"),
            Bandwidth: Field("Banda", @"\bIP\b"),
            Ip: Field(@"\bIP\b", @"\bVendedor\b"),
            Seller: Regex.Match(text, @"Vendedor\s*(.+)$", RegexOptions.Singleline) is { Success: true } v
                ? v.Groups[1].Value.Trim()
                : null);
    }

    /// <summary>Trimmed text, or null for the placeholders the report prints when a value is missing ("null", "-"...).</summary>
    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.ToLowerInvariant() is "null" or "undefined" or "n/a" or "-" or "—" ? null : text;
    }

    /// <summary>"... no bairro null, em Cidade ..." loses the missing neighborhood.</summary>
    private static string DropEmptyNeighborhood(string text) =>
        Regex.Replace(text, @"\s+(?:no\s+)?bairro\s+null\b,?", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void CheckSubtotal(string title, List<Occurrence> items, double expected, List<string> warnings)
    {
        var sum = items.Sum(o => Math.Round(o.Material.Quantity * o.Material.UnitPrice, 2));
        if (Math.Abs(sum - expected) > PriceTolerance * Math.Max(1, items.Count))
            warnings.Add($"O subtotal de \"{title}\" no PDF é {expected:0.00}, mas os itens lidos somam " +
                         $"{sum:0.00}. Algum material pode ter ficado de fora.");
    }

    /// <summary>The city is the first part of the subtitle ("City · Neighborhood · ..."); the date is when it was generated.</summary>
    private static ProjectInfo ReadProjectInfo(IReadOnlyList<string> lines)
    {
        string? city = null;
        string? date = null;
        foreach (var line in lines)
        {
            if (Summary().IsMatch(line))
                break;
            if (date is null && GeneratedOn().Match(line) is { Success: true } generated)
                date = generated.Groups[1].Value;
            if (city is null && Subtitle().Match(line) is { Success: true } subtitle)
                city = subtitle.Groups[1].Value.Trim();
        }
        return new ProjectInfo(null, city, date);
    }

    private static bool IsPageDecoration(string line) =>
        line.StartsWith("ROUTE MAP", StringComparison.Ordinal)
        || line.StartsWith("Master Internet", StringComparison.Ordinal);

    /// <summary>Brazilian format: "1.613" and "R$ 17,21" use the dot for thousands and the comma for decimals.</summary>
    private static double ParseNumber(string text) =>
        double.Parse(text.Replace(".", string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^Material\s+Qtd\.\s+Un\.\s+Valor unit\.\s+Total$")]
    private static partial Regex TableHeader();

    [GeneratedRegex(@"^Subtotal\s+R\$\s*(?<total>[\d.]+,\d{2})$")]
    private static partial Regex SubtotalLine();

    // The name may be empty (it was on the previous lines) and may itself end in digits ("ABRAÇADEIRA BAP 3").
    [GeneratedRegex(@"^(?<name>.*?)(?:^|\s+)(?<qty>\d{1,3}(?:\.\d{3})*(?:,\d+)?|\d+(?:,\d+)?)\s+(?<unit>\p{L}+)\s+R\$\s*(?<price>[\d.]+,\d{2})\s+R\$\s*(?<total>[\d.]+,\d{2})$")]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^RESUMO E OBJETIVO", RegexOptions.IgnoreCase)]
    private static partial Regex Summary();

    [GeneratedRegex(@"Gerado em\s+(\d{2}/\d{2}/\d{4})")]
    private static partial Regex GeneratedOn();

    [GeneratedRegex(@"^([^·]+?)\s*·[^·]+·")]
    private static partial Regex Subtitle();
}
