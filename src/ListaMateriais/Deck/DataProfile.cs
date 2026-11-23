using System.Globalization;
using ListaMateriais.Kmz;

namespace ListaMateriais.Deck;

internal enum ColumnKind
{
    Empty,

    /// <summary>Codes, documents, phones, coordinates: never summed nor grouped.</summary>
    Ignore,

    Date,

    /// <summary>A number worth adding up: a value, an amount, a count.</summary>
    Measure,

    /// <summary>A few values that repeat: city, seller, product, status.</summary>
    Category,

    Text,
}

/// <summary>One column and what it was read as.</summary>
internal sealed class DataColumn
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public ColumnKind Kind { get; set; }

    /// <summary>Title words, upper case without accents.</summary>
    public required string[] Words { get; init; }

    public double?[] Numbers { get; set; } = [];

    public DateTime?[] Dates { get; set; } = [];

    public bool Money { get; set; }

    public bool Percent { get; set; }

    public int Distinct { get; set; }

    /// <summary>How well it serves its kind (a better measure, a more telling category).</summary>
    public double Score { get; set; }

    public bool Has(params string[] words) => Words.Any(w => words.Contains(w)) || words.Any(k => Words.Any(w => w.StartsWith(k, StringComparison.Ordinal) && k.Length >= 5));
}

/// <summary>How the main number is written: money, a count of something, a percentage.</summary>
internal sealed record ValueFormat(bool Money, bool Percent, bool Whole, string Noun)
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly ValueFormat Count = new(false, false, true, "registros");

    /// <summary>"R$ 1,25 mi", "10.632 portas", "45%".</summary>
    public string Format(double value, bool withNoun = true)
    {
        if (Percent)
            return value.ToString("0.#%", Br);
        if (Money)
            return "R$ " + Short(value, 2);
        var number = Math.Abs(value) >= 1_000_000 ? Short(value, 2) : Whole || Math.Abs(value) >= 1000 ? value.ToString("N0", Br) : value.ToString("N1", Br);
        return withNoun && Noun.Length > 0 ? $"{number} {Noun}" : number;
    }

    /// <summary>1,25 mi; 845 mil; 8.450; 845,50.</summary>
    private static string Short(double value, int decimals)
    {
        var a = Math.Abs(value);
        return a >= 1e9 ? (value / 1e9).ToString("0.##", Br) + " bi"
            : a >= 1e6 ? (value / 1e6).ToString("0.##", Br) + " mi"
            : a >= 1e4 ? (value / 1e3).ToString("0", Br) + " mil"
            : a >= 100 ? value.ToString("N0", Br)
            : value.ToString("N" + decimals, Br);
    }
}

/// <summary>What a table holds, as far as can be told from its titles and its values.</summary>
internal sealed class DataProfile
{
    private static readonly string[] IgnoreWords =
    [
        "ID", "CPF", "CNPJ", "CPFCNPJ", "CEP", "TELEFONE", "FONE", "CELULAR", "WHATSAPP", "CODIGO", "COD", "MATRICULA", "PROTOCOLO",
        "CONTRATO", "LAT", "LATITUDE", "LONG", "LONGITUDE", "LON", "LNG", "EMAIL", "NUMERO", "NO", "N", "NRO", "SEQ", "SEQUENCIA",
        "INSC", "INSCRICAO", "RG", "CHAVE", "HASH", "URL", "LINK", "IP", "MAC", "SERIAL", "SERIE", "IDGEOGRID", "IDPORTA", "IDITEMREDE",
        "IDINTEGRADOR", "ENDERECO", "LOGRADOURO", "COMPLEMENTO", "OBSERVACAO", "OBS", "DESCRICAO", "PORTA", "SLOT", "PLACA",
    ];

    /// <summary>Dates that mark when something happened (a sale, an activation): worth a month-by-month chart.</summary>
    private static readonly string[] EventDateWords =
    [
        "DATA", "DT", "MES", "VENDA", "EMISSAO", "COMPETENCIA", "PERIODO", "REFERENCIA", "PEDIDO", "ATIVACAO", "INSTALACAO", "ABERTURA",
        "CADASTRO", "CRIACAO", "FECHAMENTO", "CONCLUSAO", "ENTREGA", "ANOMES", "DIA",
    ];

    /// <summary>Dates that describe a thing (when a battery was made): no story month by month.</summary>
    private static readonly string[] AttributeDateWords = ["FABRICACAO", "NASCIMENTO", "VALIDADE", "MEDICAO", "VENCIMENTO", "INICIO", "FIM", "VIGENCIA", "ADMISSAO"];

    private static readonly string[] MeasureWords =
    [
        "VALOR", "RECEITA", "FATURAMENTO", "VENDA", "VENDAS", "TOTAL", "PRECO", "MONTANTE", "CUSTO", "CUSTOS", "LUCRO", "QUANTIDADE",
        "QTD", "QTDE", "QUANT", "VOLUME", "PORTAS", "CLIENTES", "UNIDADES", "ALUGUEL", "CONSUMO", "META", "ASSINANTES", "ACESSOS", "HP", "HC",
        "SALDO", "PAGO", "PAGAMENTO", "TICKET", "DESPESA", "DESPESAS", "INVESTIMENTO", "CAPEX", "OPEX", "MRR", "ARPU", "ITENS", "PEDIDOS",
        "ENTREGAS", "INSTALACOES", "ATIVACOES", "METROS", "KM", "HORAS", "CHAMADOS", "OS",
    ];

    private static readonly string[] MoneyWords =
        ["VALOR", "RECEITA", "FATURAMENTO", "PRECO", "CUSTO", "CUSTOS", "LUCRO", "ALUGUEL", "MONTANTE", "TICKET", "DESPESA", "DESPESAS", "INVESTIMENTO", "CAPEX", "OPEX", "MRR", "ARPU", "SALDO", "PAGO", "PAGAMENTO"];

    private static readonly string[] DateWords =
    [
        "DATA", "DT", "MES", "INICIO", "FIM", "VENCIMENTO", "EMISSAO", "PERIODO", "COMPETENCIA", "DATE", "DIA", "CRIACAO", "CADASTRO",
        "ATIVACAO", "INSTALACAO", "PREVISAO", "ABERTURA", "FECHAMENTO", "CONCLUSAO", "DESMOBILIZACAO", "REFERENCIA", "ANOMES",
    ];

    private static readonly string[] CategoryWords =
    [
        "CIDADE", "MUNICIPIO", "REGIAO", "REGIONAL", "UF", "ESTADO", "VENDEDOR", "CONSULTOR", "PRODUTO", "CATEGORIA", "CANAL", "CLIENTE",
        "LOJA", "FILIAL", "EQUIPE", "SEGMENTO", "BAIRRO", "PLANO", "CLUSTER", "TIPO", "ORIGEM", "MARCA", "LINHA", "GRUPO", "SETOR", "AREA",
        "ROTA", "POP", "PROJETO", "TECNICO", "RESPONSAVEL", "SERVICO", "MODALIDADE", "TORRE", "OLT", "PARCEIRO", "FORNECEDOR", "MOTIVO",
    ];

    internal static readonly string[] StatusWords = ["STATUS", "SITUACAO", "ETAPA", "FASE", "ANDAMENTO", "ESTAGIO", "ATENDIMENTOGEOGRID"];

    private static readonly string[] TotalLabels = ["TOTAL", "TOTAL GERAL", "SUBTOTAL", "SOMA", "TOTAIS", "GERAL"];

    private DataProfile(DataSet data, List<DataColumn> columns, List<int> rows, int droppedTotals)
    {
        Data = data;
        Columns = columns;
        Rows = rows;
        DroppedTotals = droppedTotals;
    }

    public DataSet Data { get; }

    public IReadOnlyList<DataColumn> Columns { get; }

    /// <summary>The rows used: total rows of the sheet are left out so nothing is counted twice.</summary>
    public IReadOnlyList<int> Rows { get; }

    public int DroppedTotals { get; }

    /// <summary>The number the presentation is about; null to count the rows.</summary>
    public DataColumn? Measure { get; private set; }

    public string MeasureName => Measure?.Name ?? "Registros";

    public ValueFormat Format { get; private set; } = ValueFormat.Count;

    /// <summary>The groupings worth showing, best first (the status column is apart).</summary>
    public IReadOnlyList<DataColumn> Dimensions { get; private set; } = [];

    public DataColumn? Date { get; private set; }

    public DataColumn? Status { get; private set; }

    /// <summary>The value of a row: the measure, or 1 when counting rows. Null when the row has no number.</summary>
    public double? Value(int row) => Measure is null ? 1 : Measure.Numbers[row];

    public string? Text(DataColumn column, int row) => Data.Cell(row, column.Index);

    /// <summary>
    /// A measure that is a part of the main one ("Portas ocupadas" of "Quantidade portas"): it gives a rate
    /// (occupancy, conversion). Null when there is none.
    /// </summary>
    public DataColumn? Part { get; private set; }

    /// <summary>The name to print: "Coluna A" (no title in the sheet) becomes something readable.</summary>
    public string NameOf(DataColumn column) =>
        !column.Name.StartsWith("Coluna ", StringComparison.Ordinal) ? column.Name : column == Status ? "Situação" : "Categoria";

    private static readonly string[] PartWords = ["OCUPADAS", "OCUPADOS", "OCUPADA", "USADAS", "USADOS", "VENDIDAS", "VENDIDOS", "ATIVAS", "ATIVOS", "CONCLUIDAS", "CONCLUIDOS", "ENTREGUES", "ATENDIDOS", "ATENDIDAS", "INSTALADAS", "INSTALADOS", "REALIZADO", "REALIZADA", "CONVERTIDOS"];

    private static DataColumn? FindPart(List<DataColumn> columns, DataColumn measure, DataSet data)
    {
        var shared = measure.Words.Where(w => w.Length >= 4 && !new[] { "QUANTIDADE", "TOTAL", "VALOR", "QTDE" }.Contains(w)).ToList();
        if (shared.Count == 0)
            return null;
        return columns.Where(c => c != measure && c.Kind == ColumnKind.Measure && c.Words.Any(shared.Contains) && c.Has(PartWords))
            .Where(c =>
            {
                var pairs = Enumerable.Range(0, data.Rows.Count).Where(r => c.Numbers[r] is not null && measure.Numbers[r] is not null).ToList();
                return pairs.Count > 0 && pairs.Count(r => c.Numbers[r] <= measure.Numbers[r] + 1e-9) >= pairs.Count * 0.95;
            })
            .MaxBy(c => c.Score);
    }

    public static DataProfile Analyze(DataSet data)
    {
        var columns = new List<DataColumn>();
        for (var c = 0; c < data.Headers.Count; c++)
        {
            var column = new DataColumn { Index = c, Name = data.Headers[c], Words = ColumnGuesser.Words(data.Headers[c]) };
            Classify(column, data);
            columns.Add(column);
        }

        // Total rows: a category or text cell that says "Total".
        var labels = columns.Where(c => c.Kind is ColumnKind.Category or ColumnKind.Text).ToList();
        var rows = new List<int>();
        var dropped = 0;
        for (var r = 0; r < data.Rows.Count; r++)
        {
            if (labels.Any(c => data.Cell(r, c.Index) is { } t && TotalLabels.Contains(AddressText.Plain(t).TrimEnd(':'))))
                dropped++;
            else
                rows.Add(r);
        }

        var profile = new DataProfile(data, columns, rows, dropped);
        profile.Measure = columns.Where(c => c.Kind == ColumnKind.Measure).MaxBy(c => c.Score);
        if (profile.Measure is { } measure)
            profile.Format = new ValueFormat(measure.Money, measure.Percent, measure.Numbers.All(n => n is null || Math.Abs(n.Value % 1) < 1e-9), Noun(measure));
        if (profile.Measure is { } main)
            profile.Part = FindPart(columns, main, data);
        // Only a date that marks events, and that most rows have, tells a story month by month.
        profile.Date = columns.Where(c => c.Kind == ColumnKind.Date && c.Score > 0.5 && c.Dates.Count(d => d is not null) >= data.Rows.Count * 0.5)
            .MaxBy(c => c.Score);
        profile.Status = columns.Where(c => c.Kind == ColumnKind.Category && IsStatus(c, data)).MaxBy(c => c.Score);
        profile.Dimensions = [.. columns.Where(c => c.Kind == ColumnKind.Category && c != profile.Status).OrderByDescending(c => c.Score)];
        return profile;
    }

    private static void Classify(DataColumn column, DataSet data)
    {
        var values = Enumerable.Range(0, data.Rows.Count).Select(r => data.Cell(r, column.Index)).ToArray();
        var filled = values.Where(v => v is not null).Select(v => v!).ToList();
        if (filled.Count == 0)
        {
            column.Kind = ColumnKind.Empty;
            return;
        }
        var share = filled.Count / (double)values.Length;
        column.Distinct = filled.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var ignore = column.Has(IgnoreWords) && !column.Has(MeasureWords);

        // Dates: by title (Excel serial numbers too) or because nearly every value reads as one.
        var dateTitle = column.Has(DateWords);
        var dates = values.Select(v => Dates.TryParse(v, serials: dateTitle, out var d) ? d : (DateTime?)null).ToArray();
        var dateShare = dates.Count(d => d is not null) / (double)filled.Count;
        if (!dateTitle && dateShare < 0.8)
        {
            // Serial numbers in a column titled otherwise ("DESMOBILIZAÇÃO"): whole numbers in 1998–2050.
            var serials = filled.Count(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n % 1 == 0 && n is >= 36000 and <= 55000);
            if (serials >= filled.Count * 0.9 && !column.Has(MeasureWords) && !ignore)
            {
                dates = values.Select(v => Dates.TryParse(v, serials: true, out var d) ? d : (DateTime?)null).ToArray();
                dateShare = 1;
            }
        }
        if (dateShare >= 0.8 && (dateTitle || !column.Has(MeasureWords)))
        {
            column.Kind = ColumnKind.Date;
            column.Dates = dates;
            column.Score = share + (column.Has(EventDateWords) ? 1 : 0) - (column.Has(AttributeDateWords) ? 2 : 0);
            return;
        }
        if (ignore)
        {
            column.Kind = ColumnKind.Ignore;
            return;
        }

        // Measures: numbers with a title that says so, money, or real decimals. Whole numbers without such a title are codes.
        var money = false;
        var percent = false;
        var numbers = values.Select(v =>
        {
            if (!Numbers.TryParse(v, out var n, out var m, out var p))
                return (double?)null;
            money |= m;
            percent |= p;
            return n;
        }).ToArray();
        var numeric = numbers.Count(n => n is not null) / (double)filled.Count;
        var decimals = numbers.Count(n => n is { } x && Math.Abs(x % 1) > 1e-9) / (double)Math.Max(1, filled.Count);
        var titled = column.Has(MeasureWords);
        var counts = numbers.Where(n => n is not null).Select(n => n!.Value).ToList();
        var looksLikeCounts = counts.Count > 0 && counts.All(n => n >= 0 && n < 1e7 && n % 1 == 0)
                              && counts.Distinct().Count() < counts.Count * 0.6 && counts.Count >= 5;
        if (numeric >= 0.8 && (titled || money || percent || decimals >= 0.2 || looksLikeCounts))
        {
            column.Kind = ColumnKind.Measure;
            column.Numbers = numbers;
            column.Money = money || column.Has(MoneyWords);
            column.Percent = percent && !money;
            // A share or a rate cannot be added up: it stays a measure only if nothing better exists.
            column.Score = share + (titled ? 2 : 0) + 0.4 * column.Words.Count(w => MeasureWords.Contains(w)) + (column.Money ? 1.5 : 0) + (column.Percent ? -2 : 0)
                           + (column.Has("VALOR", "RECEITA", "FATURAMENTO", "VENDAS", "VENDA") ? 1 : 0) - (column.Has("UNITARIO", "MEDIA", "MEDIO") ? 1.5 : 0)
                           // The whole, not a part of it: "Quantidade portas" before "Portas ocupadas" or "Total portas reservadas".
                           - (column.Has(PartWords) ? 0.6 : 0) - 0.15 * Math.Max(0, column.Words.Length - 2) - (looksLikeCounts && !titled ? 1.5 : 0);
            return;
        }
        if (numeric >= 0.8)
        {
            column.Kind = ColumnKind.Ignore;
            return;
        }

        // Categories: values that repeat. Free text (names, addresses, remarks) does not.
        var limit = filled.Count >= 1000 ? Math.Min(400, filled.Count / 10) : Math.Max(2, Math.Min(60, (int)(filled.Count * 0.5)));
        var titledCategory = column.Has(CategoryWords) || column.Has(StatusWords);
        var label = titledCategory && column.Distinct == filled.Count && filled.Count is >= 2 and <= 500;
        if (column.Distinct >= 2 && (column.Distinct <= limit || label) && share >= 0.3)
        {
            column.Kind = ColumnKind.Category;
            column.Score = share + (titledCategory ? 2 : 0) + (column.Distinct is >= 3 and <= 40 ? 1 : 0)
                           + (column.Has("CIDADE", "MUNICIPIO", "REGIAO", "REGIONAL", "VENDEDOR", "PRODUTO", "CANAL", "CLIENTE") ? 1.2 : 0)
                           - (column.Distinct > 100 ? 0.5 : 0);
            return;
        }
        column.Kind = ColumnKind.Text;
    }

    private static bool IsStatus(DataColumn column, DataSet data)
    {
        if (column.Distinct > 8)
            return false;
        if (column.Has(StatusWords))
            return true;
        var values = Enumerable.Range(0, data.Rows.Count).Select(r => data.Cell(r, column.Index)).OfType<string>().Take(500).ToList();
        return values.Count(v => StatusOf(v) != StatusTone.Neutral) >= values.Count * 0.5;
    }

    /// <summary>Whether a status means done, in progress, or a problem.</summary>
    public static StatusTone StatusOf(string value)
    {
        var plain = AddressText.Plain(value);
        if (new[] { "CONCLU", "ENTREG", "FINALIZ", "ATIV", "PAGO", "OK", "APROV", "INSTALAD", "CADASTRAD", "REALIZ", "FECHAD", "GANH", "ATENDID", "LIBERAD", "SUCESSO" }
            .Any(w => plain.StartsWith(w, StringComparison.Ordinal) || plain.Contains(" " + w, StringComparison.Ordinal)))
            return plain.Contains("NAO ", StringComparison.Ordinal) || plain.StartsWith("IN", StringComparison.Ordinal) ? StatusTone.Problem : StatusTone.Done;
        if (new[] { "ANDAMENTO", "PENDENTE", "AGUARD", "PREVIST", "EXECU", "ABERT", "PLANEJ", "PROGRAM", "ANALISE", "PROCESS", "NOVO" }
            .Any(w => plain.Contains(w, StringComparison.Ordinal)))
            return StatusTone.Pending;
        if (new[] { "CANCEL", "ATRAS", "ERRO", "FALH", "REPROV", "PERDID", "DESMOBILIZ", "INATIV", "SUSPEN", "BLOQUE", "RETIRAD", "VENCID" }
            .Any(w => plain.Contains(w, StringComparison.Ordinal)))
            return StatusTone.Problem;
        return StatusTone.Neutral;
    }

    /// <summary>"Portas" → "portas"; "Qtd vendas" → "vendas"; "Quantidade" → "" (the number speaks).</summary>
    private static string Noun(DataColumn measure)
    {
        if (measure.Money || measure.Percent)
            return string.Empty;
        var words = measure.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var last = words.LastOrDefault(w => w.Length > 2 && char.IsLetter(w[0]));
        if (last is null)
            return string.Empty;
        var plain = AddressText.Plain(last);
        return plain.EndsWith('S') && plain is not ("QTDS" or "TOTAIS") && !new[] { "QUANTIDADE", "QTD", "QTDE", "TOTAL" }.Contains(plain)
            ? last.ToLower(CultureInfo.GetCultureInfo("pt-BR"))
            : string.Empty;
    }
}

internal enum StatusTone
{
    Neutral,
    Done,
    Pending,
    Problem,
}
