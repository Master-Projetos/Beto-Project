using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ListaMateriais.Planilhas;

namespace ListaMateriais.Tests;

// All data here is made up.
public sealed class WorkbookTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly Color Yellow = Color.FromArgb(0xFF, 0xFF, 0x00);
    private readonly string _folder = Directory.CreateTempSubdirectory("planilha-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Text(string reference, string text) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    private static string Sheet(string rows, string after = "") =>
        $"<worksheet xmlns=\"{Main}\"><sheetData>{rows}</sheetData>{after}</worksheet>";

    /// <summary>Title, header on row 2, two full data rows, a blank row 5 and a row with the city only.</summary>
    private static string Sales => Sheet(
        $"<row r=\"1\">{Text("A1", "Relatório")}</row>" +
        $"<row r=\"2\">{Text("A2", "Cidade")}{Text("B2", "Situação")}{Text("C2", "Valor")}{Text("D2", "Data")}</row>" +
        $"<row r=\"3\">{Text("A3", "Lavras")}{Text("B3", "Concluída")}<c r=\"C3\" s=\"1\"><v>1500.5</v></c><c r=\"D3\" s=\"2\"><v>46023</v></c></row>" +
        $"<row r=\"4\">{Text("A4", "Betim")}{Text("B4", "Cancelada")}<c r=\"C4\" s=\"1\"><v>200</v></c><c r=\"D4\" s=\"2\"><v>46100</v></c></row>" +
        $"<row r=\"6\" spans=\"1:1\">{Text("A6", "Lavras")}</row>",
        "<mergeCells count=\"1\"><mergeCell ref=\"A1:D1\"/></mergeCells>");

    /// <summary>Same column names in another order and spelling, no cell references, and a table.</summary>
    private static string Other => Sheet(
        $"<row>{Text("A1", "situacao")}{Text("B1", "CIDADE")}</row>" +
        "<row><c t=\"inlineStr\"><is><t>Cancelada</t></is></c><c t=\"inlineStr\"><is><t>Itaúna</t></is></c></row>" +
        $"<row>{Text("A3", "Concluída")}{Text("B3", "Lavras")}</row>",
        "<tableParts count=\"0\"/>");

    private string Build(params (string Name, string Xml)[] sheets)
    {
        var path = Path.Combine(_folder, "entrada.xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(xml));
        }
        const string rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Part("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{rels}\"><sheets>" +
            string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.Name}\" sheetId=\"{i + 1}\" r:id=\"r{i + 1}\"/>")) +
            "</sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"r{i + 1}\" Type=\"{rels}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            "</Relationships>");
        Part("xl/styles.xml", $"<styleSheet xmlns=\"{Main}\"><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill></fills><cellXfs count=\"3\"><xf numFmtId=\"0\" fillId=\"0\"/>" +
            "<xf numFmtId=\"4\" fillId=\"0\"/><xf numFmtId=\"14\" fillId=\"0\"/></cellXfs></styleSheet>");
        for (var i = 0; i < sheets.Length; i++)
            Part($"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
        return path;
    }

    private static byte[] Bytes(string path, string part)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(part)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static XElement Xml(string path, string part) => XDocument.Load(new MemoryStream(Bytes(path, part))).Root!;

    private static XElement? Cell(XElement sheet, string reference) =>
        sheet.Descendants(Main + "c").FirstOrDefault(c => (string?)c.Attribute("r") == reference);

    private static string[] Hidden(XElement sheet) =>
        [.. sheet.Descendants(Main + "row").Where(r => (string?)r.Attribute("hidden") == "1").Select(r => (string)r.Attribute("r")!)];

    private static Condition City(string value) => new("Cidade", "É igual a", value);

    [Fact]
    public void Finds_the_header_and_the_columns_of_each_sheet()
    {
        var book = Workbook.Open(Build(("Vendas", Sales), ("Outra", Other)));

        Assert.Equal(2, book.Sheets[0].FindHeaderRow());
        Assert.Equal(["Cidade", "Situação", "Valor", "Data"], book.Sheets[0].Columns().Select(c => c.Name));
        Assert.Equal(["situacao", "CIDADE"], book.Sheets[1].Columns().Select(c => c.Name));
        Assert.Equal("01/01/2026", book.Sheets[0].Rows[3][3]);
        Assert.Equal("1500,5", book.Sheets[0].Rows[3][2]);
    }

    [Fact]
    public void Paints_and_filters_only_the_chosen_sheets()
    {
        var input = Build(("Vendas", Sales), ("Outra", Other), ("Fora", Sales));
        var original = File.ReadAllBytes(input);
        var output = Path.Combine(_folder, "saida.xlsx");
        var job = new Job(["Vendas", "Outra"],
            [new PaintRule(["Cidade", "Valor"], Yellow, [City("lavras")])],
            [City("Lavras")]);

        var result = Workbook.Open(input).Save(job, output);

        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(Bytes(input, "xl/worksheets/sheet3.xml"), Bytes(output, "xl/worksheets/sheet3.xml"));
        var sales = Xml(output, "xl/worksheets/sheet1.xml");
        Assert.Equal(["4", "5"], Hidden(sales));
        Assert.Equal("A2:D6", (string?)sales.Element(Main + "autoFilter")?.Attribute("ref"));
        Assert.Equal(["sheetData", "autoFilter", "mergeCells"], sales.Elements().Select(e => e.Name.LocalName));
        // The empty C6 is created and painted; the row keeps its cells in order.
        Assert.Equal(["A6", "C6"], sales.Descendants(Main + "c").Select(c => (string)c.Attribute("r")!).Where(r => r.EndsWith('6')));
        Assert.Null(Cell(sales, "A4")!.Attribute("s"));

        var other = Xml(output, "xl/worksheets/sheet2.xml");
        Assert.Equal(["2"], Hidden(other));
        Assert.Null(other.Element(Main + "autoFilter"));
        Assert.NotNull(Cell(other, "B3")!.Attribute("s"));

        var styles = Xml(output, "xl/styles.xml");
        var fills = styles.Element(Main + "fills")!;
        Assert.Equal(3, fills.Elements().Count());
        Assert.Equal("3", (string?)fills.Attribute("count"));
        Assert.Equal("FFFFFF00", (string?)fills.Descendants(Main + "fgColor").Single().Attribute("rgb"));
        // A painted number keeps its number format.
        var painted = styles.Element(Main + "cellXfs")!.Elements().ElementAt((int)Cell(sales, "C3")!.Attribute("s")!);
        Assert.Equal("4", (string?)painted.Attribute("numFmtId"));
        Assert.Equal("2", (string?)painted.Attribute("fillId"));
        // The second sheet has no "Valor": said, and the rest is still done.
        Assert.Equal("Aba \"Outra\": coluna \"Valor\" não encontrada (pintura 1).", Assert.Single(result.Warnings));
        // The result opens again.
        Assert.Equal(3, Workbook.Open(output).Sheets.Count);
    }

    [Theory]
    [InlineData("Valor", "Maior que", "1000", 3)]
    [InlineData("Data", "Maior que", "01/02/2026", 4)]
    [InlineData("Situação", "Está vazio", "", 5)]
    public void Compares_numbers_dates_and_blank_cells(string column, string op, string value, int shown)
    {
        var book = Workbook.Open(Build(("Vendas", Sales)));
        var output = Path.Combine(_folder, "saida.xlsx");

        var result = book.Save(new Job(["Vendas"], [], [new Condition(column, op, value)]), output);

        Assert.True(result.Changes);
        var hidden = Hidden(Xml(output, "xl/worksheets/sheet1.xml"));
        Assert.NotEmpty(hidden);
        Assert.DoesNotContain(shown.ToString(CultureInfo.InvariantCulture), hidden);
    }

    [Fact]
    public void Warns_instead_of_failing_and_writes_nothing_when_nothing_changes()
    {
        var input = Build(("Vendas", Sales), ("Vazia", Sheet("")), ("Quebrada", "<worksheet"));
        var output = Path.Combine(_folder, "saida.xlsx");
        var job = new Job(["Vendas", "Vazia", "Quebrada"],
            [new PaintRule(["Não existe"], Yellow, []), new PaintRule(["Cidade"], Yellow, [City("Nenhuma")])],
            [new Condition("Outra coluna", "É igual a", "x")]);

        var result = Workbook.Open(input).Save(job, output);

        Assert.False(result.Changes);
        Assert.False(File.Exists(output));
        Assert.Contains(result.Warnings, w => w.Contains("Não existe", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("nenhuma linha", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Vazia", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("Quebrada", StringComparison.Ordinal));
    }

    [Fact]
    public void A_switched_off_condition_does_not_change_how_the_others_are_joined()
    {
        var result = Workbook.Open(Build(("Vendas", Sales))).Plan(new Job(["Vendas"], [],
            [City("Lavras"), new Condition("Valor", "Maior que", "10", Or: true, Active: false), new Condition("Situação", "É igual a", "Concluída")]));

        Assert.Equal(1, result.Sheets[0].Shown);
    }

    [Fact]
    public void Several_picked_values_paint_all_their_rows_at_once()
    {
        var (op, value) = Pages.FilterRuleControls.Combine(["Lavras", "Betim"])!.Value;

        var result = Workbook.Open(Build(("Vendas", Sales)))
            .Plan(new Job(["Vendas"], [new PaintRule(["Cidade"], Yellow, [new Condition("Cidade", op, value)])], []));

        Assert.Equal(3, result.Sheets[0].PaintedCells);
        Assert.Equal(("É igual a", "Betim"), Pages.FilterRuleControls.Combine(["Betim"]));
        Assert.Null(Pages.FilterRuleControls.Combine(["a;b", "c"]));
    }

    [Fact]
    public void A_filter_that_matches_nothing_hides_nothing()
    {
        var result = Workbook.Open(Build(("Vendas", Sales))).Plan(new Job(["Vendas"], [], [City("Nenhuma")]));

        Assert.False(result.Changes);
        Assert.Equal(0, result.Sheets[0].Hidden);
    }

    [Fact]
    public void Refuses_to_save_over_the_original_and_explains_an_invalid_file()
    {
        var input = Build(("Vendas", Sales));
        Assert.Throws<AppException>(() => Workbook.Open(input).Save(new Job(["Vendas"], [], [City("Betim")]), input));

        var broken = Path.Combine(_folder, "ruim.xlsx");
        File.WriteAllText(broken, "isto não é uma planilha");
        Assert.Throws<AppException>(() => Workbook.Open(broken));
        Assert.Throws<AppException>(() => Workbook.Open(Path.Combine(_folder, "nao-existe.xlsx")));
    }
}
