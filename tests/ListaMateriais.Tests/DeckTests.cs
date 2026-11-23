using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using ListaMateriais.Deck;

namespace ListaMateriais.Tests;

// All data here is made up.
internal static class Sales
{
    public static readonly string[] Cities = ["Lavras", "Varginha", "Divinópolis", "Itaúna", "Betim"];
    private static readonly string[] Sellers = ["Ana", "Bruno", "Carla", "Diego"];
    private static readonly string[] States = ["Concluída", "Concluída", "Concluída", "Em andamento", "Cancelada"];

    /// <summary>A sales sheet: a title above the header, money as text, a document column, and a total row at the end.</summary>
    public static List<string?[]> Rows(int count = 400, DateTime? last = null)
    {
        var end = last ?? new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Unspecified);
        var random = new Random(7);
        var rows = new List<string?[]>
        {
            new string?[] { "Relatório de vendas 2026" },
            new string?[] { null },
            new string?[] { "Data", "Cliente CPF", "Cidade", "Vendedor", "Situação", "Valor" },
        };
        for (var i = 0; i < count; i++)
        {
            var date = end.AddDays(-i * 270.0 / count);
            var city = Cities[i % 7 == 0 ? 0 : random.Next(Cities.Length)];
            var value = 500 + random.Next(4500) + (city == "Lavras" ? 3000 : 0);
            rows.Add([date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), $"{random.Next(100, 999)}.{random.Next(100, 999)}.000-00", city,
                Sellers[random.Next(Sellers.Length)], States[random.Next(States.Length)], $"R$ {value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"))}"]);
        }
        rows.Add([null, null, "Total", null, null, "R$ 9.999.999,00"]);
        return rows;
    }

    public static DataProfile Profile(int count = 400, DateTime? last = null) => DataProfile.Analyze(DataSource.FromRows("Vendas", Rows(count, last))!);
}

public class DeckDataTests
{
    [Theory]
    [InlineData("R$ 1.234,56", 1234.56, true)]
    [InlineData("R$ 0,00", 0, true)]
    [InlineData("1.234.567", 1234567, false)]
    [InlineData("1234.5", 1234.5, false)]
    [InlineData("12,5", 12.5, false)]
    [InlineData("(300,00)", -300, false)]
    [InlineData("45%", 0.45, false)]
    public void Reads_numbers_as_written_in_sheets(string text, double value, bool money)
    {
        Assert.True(Numbers.TryParse(text, out var n, out var m));
        Assert.Equal(value, n, 6);
        Assert.Equal(money, m);
        Assert.False(Numbers.TryParse("-", out _, out _));
        Assert.False(Numbers.TryParse("SP-ADA", out _, out _));
    }

    [Fact]
    public void Reads_dates_as_written_in_sheets()
    {
        Assert.True(Dates.TryParse("31/12/2026", false, out var d) && d == new DateTime(2026, 12, 31));
        Assert.True(Dates.TryParse("2026-03-05T10:00:00", false, out d) && d.Month == 3);
        Assert.True(Dates.TryParse("mar/26", false, out d) && d == new DateTime(2026, 3, 1));
        Assert.True(Dates.TryParse("45777", true, out d) && d.Year == 2025);
        Assert.False(Dates.TryParse("45777", false, out _));
        Assert.False(Dates.TryParse("Lavras", false, out _));
    }

    [Fact]
    public void Finds_the_header_below_a_title_and_drops_the_total_row()
    {
        var p = Sales.Profile();

        Assert.Equal(["Data", "Cliente CPF", "Cidade", "Vendedor", "Situação", "Valor"], p.Data.Headers);
        Assert.Equal("Valor", p.Measure!.Name);
        Assert.True(p.Format.Money);
        Assert.Equal("Data", p.Date!.Name);
        Assert.Equal("Situação", p.Status!.Name);
        Assert.Equal(["Cidade", "Vendedor"], p.Dimensions.Select(d => d.Name));
        Assert.Equal(ColumnKind.Ignore, p.Columns.Single(c => c.Name == "Cliente CPF").Kind);
        Assert.Equal(1, p.DroppedTotals);
        Assert.Equal(400, p.Rows.Count);
    }

    [Fact]
    public void Sheets_with_the_same_columns_are_joined_with_the_sheet_as_a_column()
    {
        DataSet Sheet(string name, int rows) => new(name, ["Cliente", "Valor"],
            [.. Enumerable.Range(0, rows).Select(i => new string?[] { $"C{i}", "10" })]);

        var sets = DataSource.Combine([Sheet("Resumo", 2), Sheet("LAVRAS", 3), Sheet("BETIM", 4)], "Pendentes_por_cidade");

        var joined = sets[0];
        Assert.Equal(["Cliente", "Valor", "Cidade"], joined.Headers);
        Assert.Equal(7, joined.Rows.Count);
        Assert.Same(joined, DataSource.Best(sets));
    }

    [Fact]
    public void A_part_of_the_measure_gives_a_rate()
    {
        var rows = new List<string?[]> { new string?[] { "Cidade", "Quantidade portas", "Portas ocupadas", "Sigla" } };
        for (var i = 0; i < 30; i++)
            rows.Add([i % 2 == 0 ? "Lavras" : "Betim", "16", (i % 8).ToString(CultureInfo.InvariantCulture), $"CTO-{i}"]);
        var p = DataProfile.Analyze(DataSource.FromRows("CTOs", rows)!);

        Assert.Equal("Quantidade portas", p.Measure!.Name);
        Assert.Equal("Portas ocupadas", p.Part!.Name);
        Assert.Equal("portas", p.Format.Noun);
        var deck = DeckBuilder.Build(p, Brief.None, "ctos_12_11_26");
        Assert.Equal("Ctos", deck.Title);
        Assert.Contains(deck.Slides, s => s.Title == "Ocupação por cidade" && s.Finding.StartsWith("Ocupação de", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_csv_with_quotes_and_semicolons()
    {
        var rows = DataSource.CsvRows("Cidade;Valor\r\n\"Lavras; MG\";\"1.000,50\"\r\nBetim;\"diz \"\"oi\"\"\"\n", ';').ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(new string?[] { "Lavras; MG", "1.000,50" }, rows[1]);
        Assert.Equal("diz \"oi\"", rows[2][1]);
    }
}

public class DeckBuilderTests
{
    [Fact]
    public void A_sentence_filters_the_city_and_the_period()
    {
        var p = Sales.Profile();

        var brief = DeckBuilder.Read("Vendas de Lavras no 1º trimestre de 2026", p);

        Assert.Equal((new DateTime(2026, 1, 1), new DateTime(2026, 4, 1)), (brief.From, brief.To));
        var (column, values) = Assert.Single(brief.Filters);
        Assert.Equal(("Cidade", "Lavras"), (column.Name, Assert.Single(values)));
        var deck = DeckBuilder.Build(p, brief, "vendas");
        Assert.Equal("Vendas de Lavras no 1º trimestre de 2026", deck.Title);
        Assert.Contains(deck.Notes, n => n.Contains("Lavras", StringComparison.Ordinal));
        // One city left: no ranking by city.
        Assert.DoesNotContain(deck.Slides, s => s.Title.EndsWith("por cidade", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_periods_count_from_the_latest_date_of_the_data()
    {
        var brief = DeckBuilder.Read("último trimestre", Sales.Profile());

        Assert.Equal((new DateTime(2026, 8, 1), new DateTime(2026, 11, 1)), (brief.From, brief.To));
    }

    [Fact]
    public void Builds_every_kind_of_slide_and_marks_the_month_not_over()
    {
        var deck = DeckBuilder.Build(Sales.Profile(), Brief.None, "Vendas_2026");

        Assert.IsType<CoverSlide>(deck.Slides[0]);
        Assert.IsType<OverviewSlide>(deck.Slides[1]);
        var trend = Assert.Single(deck.Slides.OfType<ChartSlide>(), s => s.Bars.Count > 0 && s.Title.StartsWith("Evolução", StringComparison.Ordinal));
        Assert.True(trend.Vertical);
        Assert.EndsWith("*", trend.Bars[^1].Label, StringComparison.Ordinal);
        // The month not over is not compared: the last full month is September.
        Assert.StartsWith("Set/26", trend.Conclusion, StringComparison.Ordinal);
        Assert.Contains(deck.Slides, s => s.Title == "Valor por cidade" && s.Finding.StartsWith("Lavras lidera", StringComparison.Ordinal));
        Assert.Contains(deck.Slides, s => s.Title == "Situação");
        Assert.IsType<SummarySlide>(deck.Slides[^1]);
    }

    [Fact]
    public void A_sentence_that_leaves_nothing_says_so()
    {
        var p = Sales.Profile();
        Assert.Throws<AppException>(() => DeckBuilder.Build(p, DeckBuilder.Read("Vendas de 2019", p), "vendas"));
    }
}

public class PptxWriterTests
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private static DeckModel Deck() => DeckBuilder.Build(Sales.Profile(), Brief.None, "Vendas");

    [Fact]
    public void Writes_a_complete_presentation_without_a_template()
    {
        using var stream = new MemoryStream();
        var deck = Deck();
        PptxWriter.Write(stream, deck, template: null);

        using var zip = new ZipArchive(new MemoryStream(stream.ToArray()), ZipArchiveMode.Read);
        foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml", "ppt/slideMasters/slideMaster1.xml", "ppt/theme/theme1.xml", "docProps/core.xml" })
            Assert.NotNull(zip.GetEntry(part));
        var presentation = XDocument.Load(zip.GetEntry("ppt/presentation.xml")!.Open());
        Assert.Equal(deck.Slides.Count, presentation.Descendants(P + "sldId").Count());
        Assert.Equal(deck.Slides.Count, zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)));
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct().Count());
        Assert.Contains("Total de R$", new StreamReader(zip.GetEntry("ppt/slides/slide2.xml")!.Open()).ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_keeps_its_look_and_loses_its_old_slides()
    {
        // The plain presentation stands in for the client's template: 7 old slides to be replaced.
        using var first = new MemoryStream();
        PptxWriter.Write(first, Deck(), template: null);
        var deck = DeckBuilder.Build(Sales.Profile(), DeckBuilder.Read("Vendas de Lavras", Sales.Profile()), "Vendas");

        using var second = new MemoryStream();
        PptxWriter.Write(second, deck, first.ToArray());

        using var zip = new ZipArchive(new MemoryStream(second.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct().Count());
        Assert.Equal(deck.Slides.Count, zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)));
        var presentation = XDocument.Load(zip.GetEntry("ppt/presentation.xml")!.Open());
        Assert.Equal(deck.Slides.Count, presentation.Descendants(P + "sldId").Count());
        Assert.Equal("sldMasterIdLst", presentation.Root!.Elements().First().Name.LocalName);
        Assert.Equal("sldIdLst", presentation.Root.Elements().ElementAt(1).Name.LocalName);
        var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open()).ReadToEnd();
        Assert.Equal(deck.Slides.Count, types.Split("presentationml.slide+xml").Length - 1);
        Assert.Contains("slideLayout1.xml", new StreamReader(zip.GetEntry("ppt/slides/_rels/slide1.xml.rels")!.Open()).ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_text_is_shrunk_and_then_cut_to_fit()
    {
        var (text, size) = Measure.Fit("Uma frase bem comprida que não cabe de jeito nenhum numa caixa pequena", 20, false, 2, 0.4, wrap: false, minPoints: 10);

        Assert.Equal(10, size);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.True(Measure.Width(text, size, false) <= 2);
        Assert.Equal(("Curto", 20.0), Measure.Fit("Curto", 20, false, 2, 0.4, wrap: false, minPoints: 10));
    }
}
