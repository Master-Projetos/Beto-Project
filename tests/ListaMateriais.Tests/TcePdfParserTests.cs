namespace ListaMateriais.Tests;

public class TcePdfParserTests
{
    [Fact]
    public void Reads_a_table_whose_first_row_has_the_title_and_the_column_names()
    {
        var content = TcePdfParser.Parse(
        [
            "TCE-LC-DVL-4310-00",
            "Fulano de Tal",
            "10/11/2026",
            "Cidade: Divinópolis",
            "FERRAGENS UN QTD VALOR UNIT. VALOR TOTAL",
            "ABRAÇADEIRA BAP 3 UN 12 R$ 5,00 R$ 60,00",
            "CABO OPTICO 12FO M 1.613 R$ 2,10 R$ 3.387,30",
            "TOTAL R$ 3.447,30",
            "Texto qualquer depois da tabela.",
        ]);

        var table = Assert.Single(content.Tables);
        Assert.Equal("FERRAGENS", table.Title);
        Assert.Equal(["ABRAÇADEIRA BAP 3", "CABO OPTICO 12FO"], table.Items.Select(i => i.Name));
        Assert.Equal(new Material(12, "UN", 5), table.Items[0].Material);
        Assert.Equal(new Material(1613, "M", 2.10), table.Items[1].Material);
        Assert.Empty(content.Warnings);
        Assert.Equal(new ProjectInfo(4310, "Divinópolis", "10/11/2026"), content.Info);
        Assert.Null(content.Report);
    }

    [Fact]
    public void A_title_alone_on_the_line_above_names_a_table_without_column_names()
    {
        var content = TcePdfParser.Parse(
        [
            "INSTALAÇÃO CLIENTE",
            "DROP FLAT 1FO M 150 R$ 0,80 R$ 120,00",
            "CONECTOR SC/APC UN 2 R$ 3,50 R$ 7,00",
            "TOTAL: R$ 127,00",
            "Lançamento de cabos",
            "Unid. Quant. Vlr. Unit. Vlr. Total",
            "CABO 12FO M 2,5 R$ 10,00 R$ 25,00",
        ]);

        Assert.Equal(["INSTALAÇÃO CLIENTE", "LANÇAMENTO DE CABOS"], content.Tables.Select(t => t.Title));
        Assert.Equal(2, content.Tables[0].Items.Count);
        Assert.Equal(2.5, content.Tables[1].Items[0].Material.Quantity);
    }

    [Fact]
    public void Accepts_quantity_before_the_unit_and_values_without_currency()
    {
        var content = TcePdfParser.Parse(
        [
            "MATERIAL QTD UN VALOR TOTAL",
            "PARAFUSO 12 UN 1,00 12,00",
            "FITA DE AÇO 3 RL R$ - R$ -",
        ]);

        var table = Assert.Single(content.Tables);
        Assert.Equal("MATERIAL", table.Title);
        Assert.Equal(new Material(12, "UN", 1), table.Items[0].Material);
        Assert.Equal(new Material(3, "RL", 0), table.Items[1].Material);
    }

    [Fact]
    public void The_printed_total_settles_a_quantity_with_a_dot()
    {
        var content = TcePdfParser.Parse(
        [
            "CABOS UN QTD VALOR UNIT. VALOR TOTAL",
            "CABO A M 1.500 R$ 2,00 R$ 3.000,00",
            "CABO B M 1.5 R$ 2,00 R$ 3,00",
        ]);

        Assert.Equal([1500, 1.5], content.Tables[0].Items.Select(i => i.Material.Quantity));
    }

    [Fact]
    public void Warns_when_the_items_do_not_add_up_to_the_total()
    {
        var content = TcePdfParser.Parse(
        [
            "FERRAGENS UN QTD VALOR UNIT. VALOR TOTAL",
            "ALÇA UN 2 R$ 5,00 R$ 10,00",
            "TOTAL R$ 99,00",
        ]);

        Assert.Contains("FERRAGENS", Assert.Single(content.Warnings));
    }

    [Fact]
    public void Plain_text_with_numbers_is_not_a_table()
    {
        var content = TcePdfParser.Parse(
        [
            "O projeto prevê 120 portas em 3 caixas.",
            "Rua das Flores 12",
            "Prazo de 30 dias",
            "TOTAL DO INVESTIMENTO R$ 10.000,00",
        ]);

        Assert.Empty(content.Tables);
    }

    [Fact]
    public void Takes_the_project_code_from_the_file_name_when_the_text_has_none()
    {
        var content = TcePdfParser.Parse(["FERRAGENS UN QTD VALOR", "ALÇA UN 2 R$ 5,00 R$ 10,00"], "TCE-LC-DVL-4310-00-1.pdf");

        Assert.Equal(4310, content.Info.Code);
    }
}

public class RouteMapReportTests
{
    [Fact]
    public void Still_reads_the_Route_Map_report()
    {
        var content = PdfReader.Parse(
        [
            "Gerado em 10/11/2026",
            "Rede Centro",
            "Divinópolis · Centro · 120 portas",
            "RESUMO E OBJETIVO",
            "Ferragens",
            "Material Qtd. Un. Valor unit. Total",
            "ABRAÇADEIRA BAP 3 12 un R$ 5,00 R$ 60,00",
            "Subtotal R$ 60,00",
        ]);

        var table = Assert.Single(content.Tables);
        Assert.Equal("FERRAGENS", table.Title);
        Assert.Equal(new Material(12, "un", 5), table.Items[0].Material);
        Assert.NotNull(content.Report);
        Assert.Equal("Divinópolis", content.Info.City);
    }
}

public class WrappedCellTests
{
    private const double NameColumn = 200;

    /// <summary>A line whose name part stays in the name column and whose other columns start at 200.</summary>
    private static TextLine Line(double baseline, string name, string columns = "")
    {
        var words = new List<TextWord>();
        var text = string.Empty;
        void Add(string part, double left)
        {
            foreach (var word in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (text.Length > 0)
                    text += " ";
                words.Add(new TextWord(text.Length, left, left + word.Length * 5));
                text += word;
                left += word.Length * 5 + 3;
            }
        }
        Add(name, 20);
        Add(columns, NameColumn);
        return new TextLine(text, 0, baseline, 10, words);
    }

    [Fact]
    public void Top_aligned_cells_give_the_extra_lines_to_the_row_above()
    {
        var content = TcePdfParser.Parse(
        [
            Line(700, "LANÇAMENTO", "UN QTD VALOR UNIT. VALOR TOTAL"),
            Line(688, "DE CABOS"),
            Line(676, "CABO OPTICO", "M 1.613 R$ 2,10 R$ 3.387,30"),
            Line(664, "12FO"),
            Line(652, "ABRAÇADEIRA", "UN 12 R$ 5,00 R$ 60,00"),
            Line(640, "BAP 3"),
            Line(628, "TOTAL", "R$ 3.447,30"),
        ]);

        var table = Assert.Single(content.Tables);
        Assert.Equal("LANÇAMENTO DE CABOS", table.Title);
        Assert.Equal(["CABO OPTICO 12FO", "ABRAÇADEIRA BAP 3"], table.Items.Select(i => i.Name));
        Assert.Empty(content.Warnings);
    }

    [Fact]
    public void Bottom_aligned_cells_give_them_to_the_row_below()
    {
        // Excel aligns to the bottom: the numbers sit on the last line of the name, after a wider gap.
        var content = TcePdfParser.Parse(
        [
            Line(715, "LANÇAMENTO"),
            Line(703, "DE CABOS", "UN QTD VALOR UNIT. VALOR TOTAL"),
            Line(688, "CABO OPTICO"),
            Line(676, "12FO", "M 1.613 R$ 2,10 R$ 3.387,30"),
            Line(661, "PARAFUSO", "UN 8 R$ 2,00 R$ 16,00"),
            Line(646, "TOTAL", "R$ 3.403,30"),
        ]);

        var table = Assert.Single(content.Tables);
        Assert.Equal("LANÇAMENTO DE CABOS", table.Title);
        Assert.Equal(["CABO OPTICO 12FO", "PARAFUSO"], table.Items.Select(i => i.Name));
    }

    [Fact]
    public void Centered_cells_take_the_lines_around_the_numbers()
    {
        var content = TcePdfParser.Parse(
        [
            Line(700, "FERRAGENS", "UN QTD VALOR UNIT. VALOR TOTAL"),
            Line(685, "ALÇA PREFORMADA"),
            Line(679, "", "UN 40 R$ 1,25 R$ 50,00"),
            Line(673, "PARA CABO DROP"),
            Line(658, "PARAFUSO", "UN 8 R$ 2,00 R$ 16,00"),
        ]);

        Assert.Equal(["ALÇA PREFORMADA PARA CABO DROP", "PARAFUSO"], Assert.Single(content.Tables).Items.Select(i => i.Name));
    }

    [Fact]
    public void A_known_title_settles_a_title_that_wrapped_upward()
    {
        var lines = new[]
        {
            Line(715, "INSTALAÇÃO"),
            Line(703, "CLIENTE", "UN QTD VALOR UNIT. VALOR TOTAL"),
            Line(688, "ONU", "UN 1 R$ 100,00 R$ 100,00"),
        };

        Assert.Equal("CLIENTE", TcePdfParser.Parse(lines).Tables[0].Title);
        Assert.Equal("INSTALAÇÃO CLIENTE", TcePdfParser.Parse(lines, null, ["INSTALAÇÃO CLIENTE"]).Tables[0].Title);
    }

    [Fact]
    public void Text_after_the_table_is_not_part_of_the_last_name()
    {
        var content = TcePdfParser.Parse(
        [
            Line(700, "FERRAGENS", "UN QTD VALOR UNIT. VALOR TOTAL"),
            Line(688, "PARAFUSO", "UN 8 R$ 2,00 R$ 16,00"),
            Line(640, "Observações"),
            Line(628, "PARAFUSO", "em estoque na regional, conforme combinado com o cliente."),
        ]);

        Assert.Equal(["PARAFUSO"], Assert.Single(content.Tables).Items.Select(i => i.Name));
    }
}

public class PdfFileTests
{
    /// <summary>
    /// The same made-up TCE saved as PDF three times, with the cells of its tables aligned to the top,
    /// to the bottom and to the middle: long names wrap differently in each.
    /// </summary>
    [Theory]
    [InlineData("tce-top-aligned.pdf")]
    [InlineData("tce-bottom-aligned.pdf")]
    [InlineData("tce-centered.pdf")]
    public void Reads_a_tce_saved_as_pdf(string file)
    {
        var content = PdfReader.Read(Path.Combine(AppContext.BaseDirectory, "Samples", file));

        Assert.Null(content.Report);
        Assert.Equal(["FERRAGENS", "LANÇAMENTO DE CABOS", "INSTALAÇÃO CLIENTE"], content.Tables.Select(t => t.Title));
        Assert.Equal(
        [
            "ABRAÇADEIRA BAP 3", "ALÇA PREFORMADA PARA CABO DROP COM OLHAL DE AÇO GALVANIZADO REFORÇADO", "PARAFUSO M12",
            "CABO OPTICO 12FO", "DROP FLAT 1FO", "CONECTOR SC/APC", "ONU",
        ], content.Tables.SelectMany(t => t.Items).Select(i => i.Name));
        Assert.Equal(new Material(1613, "M", 2.10), content.Tables[1].Items[0].Material);
        Assert.Equal(new ProjectInfo(4310, "Divinópolis", "10/11/2026"), content.Info);
        Assert.Empty(content.Warnings);
    }
}
