using System.Globalization;
using System.Text.RegularExpressions;
using ListaMateriais.Kmz;

namespace ListaMateriais.Deck;

internal enum Tone
{
    Navy,
    Green,
    Orange,
    Muted,
}

/// <summary>A big number with what it is.</summary>
internal sealed record Kpi(string Value, string Label, Tone Tone = Tone.Navy);

/// <summary>A bar: its label, its value, and the part still pending (drawn in orange after the done part).</summary>
internal sealed record BarItem(string Label, double Value, string Text, double Pending = 0, string? PendingText = null, bool Highlight = false);

internal sealed record Alert(string Headline, string Text);

/// <summary>The box on the right of a chart slide.</summary>
/// <param name="Progress">0–1 for the progress bar; null for none.</param>
internal sealed record Panel(string Title, IReadOnlyList<Kpi> Kpis, double? Progress = null, string? ProgressLabel = null, string? RestLabel = null, Alert? Alert = null);

internal abstract record DeckSlide(string Title, string Finding);

internal sealed record CoverSlide(string Title, string Subtitle, string Footer) : DeckSlide(Title, Subtitle);

internal sealed record OverviewSlide(string Title, IReadOnlyList<Kpi> Kpis, IReadOnlyList<string> Points, string Conclusion) : DeckSlide(Title, Conclusion);

/// <param name="Max">The value of a full bar; null for the largest bar (rates use 1, i.e. 100%).</param>
internal sealed record ChartSlide(string Title, string Caption, IReadOnlyList<BarItem> Bars, bool Vertical, Panel Panel, string Conclusion, double? Max = null)
    : DeckSlide(Title, Conclusion);

internal sealed record SummarySlide(string Title, IReadOnlyList<string> Points, string Conclusion) : DeckSlide(Title, Conclusion);

/// <summary>The presentation, ready to draw.</summary>
/// <param name="Notes">What the user should know: filters applied, rows left out, a file read only in part.</param>
internal sealed record DeckModel(string Title, IReadOnlyList<DeckSlide> Slides, IReadOnlyList<string> Notes);

/// <summary>What the user's sentence asks for: a period, some values of a column (a city...), and the title.</summary>
internal sealed record Brief(string? Title, DateTime? From, DateTime? To, string? Period, IReadOnlyList<(DataColumn Column, IReadOnlyList<string> Values)> Filters)
{
    public static readonly Brief None = new(null, null, null, null, []);
}

/// <summary>
/// Turns a table into the findings of a presentation, with no input needed: totals, who leads and by how much,
/// how concentrated it is, how it moves month by month, how far along the work is. Only what the data shows is
/// said; causes are never made up.
/// </summary>
internal static partial class DeckBuilder
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private const int MaxBars = 7;

    private static readonly string[] MonthNames = ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];
    private static readonly string[] MonthShort = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    // ---- The sentence -----------------------------------------------------------------------------

    /// <summary>Reads the period and the filters out of a free sentence; relative periods count from the latest date of the data.</summary>
    public static Brief Read(string? description, DataProfile profile)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Brief.None;
        var text = description.Trim();
        var plain = " " + AddressText.Plain(text) + " ";
        var latest = profile.Date is { } date ? profile.Rows.Select(r => date.Dates[r]).Where(d => d is not null).DefaultIfEmpty(null).Max() : null;
        var (from, to, period) = Period(plain, latest);

        var filters = new List<(DataColumn, IReadOnlyList<string>)>();
        foreach (var column in profile.Dimensions.Append(profile.Status).OfType<DataColumn>())
        {
            var values = profile.Rows.Select(r => profile.Text(column, r)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(v => v.Length >= 3 && Regex.IsMatch(plain, @"[^A-Z0-9]" + Regex.Escape(AddressText.Plain(v)) + @"[^A-Z0-9]"))
                .ToList();
            // A word that matches half the values is not a choice (e.g. "Cadastrado" in every row's status).
            if (values.Count > 0 && values.Count <= Math.Max(1, column.Distinct / 2))
                filters.Add((column, values));
        }
        var title = char.ToUpper(text[0], Br) + text[1..];
        return new Brief(title.Length > 90 ? title[..90] + "…" : title, from, to, period, filters);
    }

    private static (DateTime? From, DateTime? To, string? Label) Period(string plain, DateTime? latest)
    {
        var anchor = latest ?? DateTime.Today;
        DateTime Month(int year, int month) => new(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var yearMatch = YearPattern().Match(plain);
        var year = yearMatch.Success ? int.Parse(yearMatch.Value, CultureInfo.InvariantCulture) : anchor.Year;

        if (LastPattern().Match(plain) is { Success: true } last)
        {
            var n = last.Groups["n"].Success ? int.Parse(last.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
            var unit = last.Groups["unit"].Value;
            var count = unit.StartsWith("TRIMESTRE", StringComparison.Ordinal) ? 3 * n : unit.StartsWith("SEMESTRE", StringComparison.Ordinal) ? 6 * n
                : unit.StartsWith("ANO", StringComparison.Ordinal) ? 12 * n : n;
            var end = Month(anchor.Year, anchor.Month).AddMonths(1);
            var start = end.AddMonths(-count);
            return (start, end, $"{Label(start)} a {Label(end.AddMonths(-1))}");
        }
        if (QuarterPattern().Match(plain) is { Success: true } quarter)
        {
            var q = Ordinal(quarter.Groups["n"].Value);
            var half = quarter.Groups["unit"].Value.StartsWith("SEMESTRE", StringComparison.Ordinal);
            var size = half ? 6 : 3;
            if (q >= 1 && q <= (half ? 2 : 4))
            {
                var start = Month(year, (q - 1) * size + 1);
                return (start, start.AddMonths(size), $"{q}º {(half ? "semestre" : "trimestre")} de {year}");
            }
        }
        var months = new List<int>();
        foreach (Match m in MonthPattern().Matches(plain))
            months.Add(Array.FindIndex(MonthShort, s => AddressText.Plain(s) == m.Groups[1].Value[..3]) + 1);
        foreach (Match m in NumericMonthPattern().Matches(plain))
        {
            months.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        if (months.Count > 0)
        {
            var start = Month(year, months.Min());
            var end = Month(year, months.Max()).AddMonths(1);
            return (start, end, months.Count == 1 || months.Min() == months.Max() ? $"{MonthNames[months[0] - 1]} de {year}" : $"{MonthShort[months.Min() - 1]} a {MonthShort[months.Max() - 1]} de {year}");
        }
        if (yearMatch.Success)
            return (Month(year, 1), Month(year + 1, 1), year.ToString(CultureInfo.InvariantCulture));
        return (null, null, null);
    }

    private static int Ordinal(string text) => text switch
    {
        "1" or "PRIMEIRO" => 1,
        "2" or "SEGUNDO" => 2,
        "3" or "TERCEIRO" => 3,
        "4" or "QUARTO" => 4,
        _ => 0,
    };

    private static string Label(DateTime month) => $"{MonthShort[month.Month - 1]}/{month:yy}";

    [GeneratedRegex(@"(?<![0-9])(19[5-9][0-9]|20[0-9]{2}|21[0-4][0-9])(?![0-9])")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(?:ULTIM[OA]S?|PASSAD[OA])\s+(?:(?<n>\d{1,2})\s+)?(?<unit>MES(?:ES)?|TRIMESTRES?|SEMESTRES?|ANOS?)\b|\b(?<unit>MES|TRIMESTRE|SEMESTRE|ANO)\s+PASSAD[OA]\b")]
    private static partial Regex LastPattern();

    [GeneratedRegex(@"\b(?<n>[1-4]|PRIMEIRO|SEGUNDO|TERCEIRO|QUARTO)\s*[OoºO°]?\s*(?<unit>TRIMESTRE|SEMESTRE)\b|\b[QT](?<n>[1-4])\b(?<unit>)")]
    private static partial Regex QuarterPattern();

    [GeneratedRegex(@"\b(JAN(?:EIRO)?|FEV(?:EREIRO)?|MAR(?:CO)?|ABR(?:IL)?|MAI(?:O)?|JUN(?:HO)?|JUL(?:HO)?|AGO(?:STO)?|SET(?:EMBRO)?|OUT(?:UBRO)?|NOV(?:EMBRO)?|DEZ(?:EMBRO)?)\b")]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"\b(0?[1-9]|1[0-2])/(20[0-9]{2})\b")]
    private static partial Regex NumericMonthPattern();

    // ---- The deck ---------------------------------------------------------------------------------

    public static DeckModel Build(DataProfile p, Brief brief, string sourceName)
    {
        var notes = new List<string>();
        var rows = p.Rows.Where(r => p.Value(r) is not null).ToList();
        if (p.Date is { } dateColumn && brief.From is { } from && brief.To is { } to)
            rows = [.. rows.Where(r => dateColumn.Dates[r] is { } d && d >= from && d < to)];
        else if (brief.From is not null)
            notes.Add("A planilha não tem coluna de data: o período da descrição não pôde ser aplicado.");
        foreach (var (column, values) in brief.Filters)
            rows = [.. rows.Where(r => p.Text(column, r) is { } v && values.Contains(v, StringComparer.OrdinalIgnoreCase))];
        if (brief.Filters.Count > 0)
            notes.Add("Filtrado por " + string.Join("; ", brief.Filters.Select(f => $"{f.Column.Name}: {string.Join(", ", f.Values)}")) + ".");
        if (brief.Period is not null && p.Date is not null)
            notes.Add($"Período: {brief.Period}.");
        if (p.DroppedTotals > 0)
            notes.Add($"{p.DroppedTotals} linha(s) de total da planilha deixada(s) de fora, para não somar em dobro.");
        if (p.Data.Truncated)
            notes.Add($"O arquivo é muito grande: só as primeiras {p.Data.Rows.Count:N0} linhas foram lidas.");
        if (rows.Count == 0)
            throw new AppException("Nenhuma linha atende à descrição (" + string.Join(" ", notes) + ").\n\nApague a descrição ou mude o período / o nome.");

        var f = p.Format;
        var total = rows.Sum(r => p.Value(r)!.Value);
        // "Portas" reads better than "Quantidade portas".
        var measureLabel = p.Measure is null ? "registros" : f.Noun.Length > 0 ? f.Noun : p.MeasureName;
        var title = brief.Title ?? CleanName(sourceName);
        var span = DateSpan(p, rows);
        var periodText = (p.Date is null ? null : brief.Period) ?? (span is var (first, last) ? first.Year == last.Year && first.Month == last.Month ? $"{MonthNames[first.Month - 1]} de {first.Year}" : $"{Label(first)} a {Label(last)}" : null);

        var slides = new List<DeckSlide>();
        var points = new List<string>();
        slides.Add(new CoverSlide(title,
            string.Join(" · ", new[] { $"Análise de {Lower(measureLabel)}", periodText, $"{rows.Count:N0} registros" }.Where(s => s is not null)!),
            $"Gerado em {DateTime.Now:dd/MM/yyyy} a partir de {sourceName}"));

        // Overview.
        var kpis = new List<Kpi> { new(f.Format(total, withNoun: false), p.Measure is null ? "registros" : $"{measureLabel.ToLower(Br)} no total") };
        if (p.Measure is not null)
        {
            kpis.Add(new Kpi(rows.Count.ToString("N0", Br), "registros", Tone.Muted));
            kpis.Add(new Kpi(f.Format(total / rows.Count, withNoun: false), "média por registro", Tone.Green));
        }
        var main = p.Dimensions.FirstOrDefault(d => Distinct(p, d, rows) > 1);
        var overviewPoints = new List<string>();
        if (main is not null)
        {
            var groups = Group(p, main, rows);
            kpis.Add(new Kpi(groups.Count.ToString("N0", Br), Plural(p.NameOf(main), groups.Count), Tone.Orange));
            overviewPoints.Add($"{groups[0].Key} lidera em {Lower(p.NameOf(main))}, com {Share(groups[0].Value, total)} do total.");
        }
        Trend? trend = p.Date is not null ? Monthly(p, rows) : null;
        if (trend is { Months.Count: >= 2 } t && t.Change is { } change)
            overviewPoints.Add($"{Capital(t.LastFull)} teve {f.Format(t.LastFullValue)}, {Signed(change)} sobre {t.PreviousFull}.");
        if (p.Status is { } statusColumn && Distinct(p, statusColumn, rows) > 1)
        {
            var done = rows.Where(r => p.Text(statusColumn, r) is { } s && DataProfile.StatusOf(s) == StatusTone.Done).Sum(r => p.Value(r)!.Value);
            if (done > 0)
                overviewPoints.Add($"{Share(done, total)} está concluído ({f.Format(done)}).");
        }
        if (periodText is not null)
            overviewPoints.Add($"Período analisado: {periodText}.");
        if (p.Part is { } overviewPart && p.Measure is { } whole)
        {
            var partTotal = rows.Sum(r => p.Part.Numbers[r] ?? 0);
            if (total > 0)
                overviewPoints.Insert(Math.Min(1, overviewPoints.Count), $"{Capital(overviewPart.Name)}: {partTotal.ToString("N0", Br)} de {f.Format(total)} ({Share(partTotal, total)}).");
        }
        var head = p.Measure is null ? $"{rows.Count:N0} registros analisados." : $"Total de {f.Format(total)} em {rows.Count:N0} registros.";
        slides.Add(new OverviewSlide("Visão geral", kpis.Take(4).ToList(), overviewPoints.Take(4).ToList(),
            overviewPoints.Count > 0 ? $"{head} {overviewPoints[0]}" : head));

        if (trend is { Months.Count: >= 2 })
        {
            var slide = TrendSlide(p, trend, f, measureLabel);
            slides.Add(slide);
            points.Add(slide.Conclusion);
        }

        if (p.Part is not null && p.Dimensions.FirstOrDefault(d => Distinct(p, d, rows) > 1) is { } rateBy)
        {
            var slide = RateSlide(p, rateBy, rows);
            slides.Add(slide);
            points.Add(slide.Conclusion);
        }

        foreach (var dimension in p.Dimensions.Where(d => Distinct(p, d, rows) > 1).Take(2))
        {
            var slide = RankingSlide(p, dimension, rows, total, f, measureLabel);
            slides.Add(slide);
            points.Add(slide.Conclusion);
        }

        if (p.Status is { } status && Distinct(p, status, rows) > 1)
        {
            var slide = StatusSlide(p, status, rows, total, f);
            slides.Add(slide);
            points.Add(slide.Conclusion);
        }

        if (points.Count > 0)
            slides.Add(new SummarySlide("Principais conclusões", points.Take(5).ToList(), head + (periodText is null ? string.Empty : $" Período: {periodText}.")));
        return new DeckModel(title, slides, notes);
    }

    // ---- Ranking ----------------------------------------------------------------------------------

    private static ChartSlide RankingSlide(DataProfile p, DataColumn dimension, List<int> rows, double total, ValueFormat f, string measureLabel)
    {
        var groups = Group(p, dimension, rows);
        var status = p.Status is { } s && Distinct(p, s, rows) > 1 ? s : null;
        double Pending(string key) => status is null ? 0
            : rows.Where(r => string.Equals(Key(p, dimension, r), key, StringComparison.OrdinalIgnoreCase)
                              && p.Text(status, r) is { } v && DataProfile.StatusOf(v) == StatusTone.Pending).Sum(r => p.Value(r)!.Value);

        var bars = new List<BarItem>();
        foreach (var (key, value) in groups.Take(groups.Count > MaxBars ? MaxBars - 1 : MaxBars))
        {
            var pending = Pending(key);
            bars.Add(new BarItem(key, value - pending, f.Format(value - pending), pending, pending > 0 ? $"{f.Format(pending)} em andamento" : null));
        }
        if (groups.Count > MaxBars)
        {
            var rest = groups.Skip(MaxBars - 1).ToList();
            bars.Add(new BarItem($"Outros ({rest.Count})", rest.Sum(g => g.Value), f.Format(rest.Sum(g => g.Value))));
        }

        var leader = groups[0];
        var average = total / groups.Count;
        var top3 = groups.Take(3).Sum(g => g.Value);
        var name = Lower(p.NameOf(dimension));
        Panel panel;
        var leaderPending = Pending(leader.Key);
        if (leaderPending > 0)
        {
            // Like a delivery report: how far the leader is along.
            var done = leader.Value - leaderPending;
            panel = new Panel($"{leader.Key} | situação", [new(f.Format(leader.Value, false), "total"), new(f.Format(done, false), "concluído", Tone.Green), new(f.Format(leaderPending, false), "andamento", Tone.Orange)],
                done / leader.Value, $"{done / leader.Value:0%} concluído", $"{1 - done / leader.Value:0%} restante", ConcentrationAlert(groups, total, name));
        }
        else
        {
            panel = new Panel($"{leader.Key} | destaque",
                [new(Share(leader.Value, total), "do total"), new($"{leader.Value / average:0.0}×".Replace('.', ','), "a média", Tone.Green), new(f.Format(leader.Value, false), Lower(measureLabel), Tone.Orange)],
                leader.Value / total, $"{leader.Value / total:0%} do total", null, ConcentrationAlert(groups, total, name));
        }

        var conclusion = groups.Count >= 3
            ? $"{leader.Key} lidera em {name} com {Share(leader.Value, total)} ({f.Format(leader.Value)}); os 3 maiores somam {Share(top3, total)} do total."
            : $"{leader.Key} lidera em {name} com {Share(leader.Value, total)} ({f.Format(leader.Value)}).";
        return new ChartSlide($"{Capital(measureLabel)} por {name}", $"{Capital(measureLabel)} por {name}", bars, false, panel, conclusion);
    }

    private static Alert? ConcentrationAlert(List<KeyValuePair<string, double>> groups, double total, string name)
    {
        if (groups.Count >= 3 && groups[0].Value / total >= 0.4)
            return new Alert($"{Share(groups[0].Value, total)} em {groups[0].Key}",
                $"Alta concentração: {groups[0].Key} sozinho tem {Share(groups[0].Value, total)}; os outros {groups.Count - 1} somam {Share(total - groups[0].Value, total)}.");
        var small = groups.Where(g => g.Value / total < 0.03).ToList();
        if (groups.Count >= 6 && small.Count >= 3)
            return new Alert($"{small.Count} com menos de 3%", $"Juntos, somam {Share(small.Sum(g => g.Value), total)} do total.");
        if (groups.Count >= 2)
        {
            var lastOne = groups[^1];
            return new Alert($"Menor: {lastOne.Key}", $"{Share(lastOne.Value, total)} do total, {groups[0].Value / Math.Max(lastOne.Value, 1e-9):0}× menos que {groups[0].Key}.");
        }
        return null;
    }

    // ---- Months ------------------------------------------------------------------------------------

    private sealed record Trend(List<(DateTime Month, double Value, bool Partial)> Months, string LastFull, double LastFullValue, string? PreviousFull, double? Change);

    private static Trend? Monthly(DataProfile p, List<int> rows)
    {
        var dated = rows.Where(r => p.Date!.Dates[r] is not null).ToList();
        if (dated.Count == 0)
            return null;
        var months = dated.GroupBy(r => { var d = p.Date!.Dates[r]!.Value; return new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Unspecified); })
            .OrderBy(g => g.Key).Select(g => (Month: g.Key, Value: g.Sum(r => p.Value(r)!.Value))).ToList();
        if (months.Count < 2)
            return new Trend([.. months.Select(m => (m.Month, m.Value, false))], Label(months[0].Month), months[0].Value, null, null);
        // The latest month is partial when the data stops well before its end (and the dates are days, not whole months).
        var latest = dated.Max(r => p.Date!.Dates[r]!.Value);
        var daily = dated.Any(r => p.Date!.Dates[r]!.Value.Day != 1);
        var partial = daily && latest.Day < DateTime.DaysInMonth(latest.Year, latest.Month) - 2;
        var list = months.TakeLast(12).Select((m, i) => (m.Month, m.Value, Partial: partial && i == Math.Min(12, months.Count) - 1)).ToList();
        var full = list.Where(m => !m.Partial).ToList();
        var lastFull = full[^1];
        (DateTime Month, double Value, bool Partial)? previous = full.Count >= 2 ? full[^2] : null;
        double? change = previous is { Value: > 0 } prev ? lastFull.Value / prev.Value - 1 : null;
        return new Trend(list, Label(lastFull.Month), lastFull.Value, previous is { } pv ? Label(pv.Month) : null, change);
    }

    private static ChartSlide TrendSlide(DataProfile p, Trend trend, ValueFormat f, string measureLabel)
    {
        // Without "R$" above each column: the title already says it is money, and the columns are narrow.
        var bars = trend.Months.Select(m => new BarItem(Label(m.Month) + (m.Partial ? "*" : string.Empty), m.Value, f.Format(m.Value, false).Replace("R$ ", string.Empty, StringComparison.Ordinal))).ToList();
        var full = trend.Months.Where(m => !m.Partial).ToList();
        var best = full.MaxBy(m => m.Value);
        var average = full.Average(m => m.Value);
        var kpis = new List<Kpi>
        {
            new(f.Format(trend.LastFullValue, false), trend.LastFull),
            new(trend.Change is { } c ? Signed(c) : "—", trend.PreviousFull is null ? "variação" : $"sobre {trend.PreviousFull}", trend.Change is < 0 ? Tone.Orange : Tone.Green),
            new(f.Format(average, false), "média mensal", Tone.Muted),
        };
        Alert? alert = null;
        if (full.Count >= 3)
        {
            var drops = full.Zip(full.Skip(1), (a, b) => (From: a, To: b, Change: a.Value > 0 ? b.Value / a.Value - 1 : 0)).ToList();
            var worst = drops.MinBy(d => d.Change);
            var growing = drops.TakeLast(3).All(d => d.Change > 0);
            alert = growing
                ? new Alert("Crescimento contínuo", $"Alta nos últimos {Math.Min(3, drops.Count)} meses seguidos.")
                : worst.Change < -0.15
                    ? new Alert($"Queda de {Math.Abs(worst.Change):0%} em {Label(worst.To.Month)}", $"De {f.Format(worst.From.Value)} em {Label(worst.From.Month)} para {f.Format(worst.To.Value)}.")
                    : new Alert($"Melhor mês: {Label(best.Month)}", $"{f.Format(best.Value)}, {Signed(best.Value / average - 1)} sobre a média.");
        }
        var conclusion = trend.Change is { } change
            ? $"{Capital(trend.LastFull)} fechou com {f.Format(trend.LastFullValue)} ({Signed(change)} sobre {trend.PreviousFull}); o melhor mês foi {Label(best.Month)}, com {f.Format(best.Value)}."
            : $"O melhor mês foi {Label(best.Month)}, com {f.Format(best.Value)}.";
        if (trend.Months.Any(m => m.Partial))
            conclusion += " *Mês ainda em andamento.";
        return new ChartSlide($"Evolução mensal | {Lower(measureLabel)}", $"{Capital(measureLabel)} por mês", bars, true,
            new Panel("Último mês completo", kpis, Alert: alert), conclusion);
    }

    // ---- Status ------------------------------------------------------------------------------------

    private static ChartSlide StatusSlide(DataProfile p, DataColumn status, List<int> rows, double total, ValueFormat f)
    {
        var groups = Group(p, status, rows);
        var bars = groups.Take(MaxBars).Select(g => new BarItem(g.Key, g.Value, $"{f.Format(g.Value)} · {Share(g.Value, total)}")).ToList();
        double Sum(StatusTone tone) => groups.Where(g => DataProfile.StatusOf(g.Key) == tone).Sum(g => g.Value);
        var done = Sum(StatusTone.Done);
        var pending = Sum(StatusTone.Pending);
        var problem = Sum(StatusTone.Problem);
        var alert = problem > 0
            ? new Alert($"{Share(problem, total)} com problema", string.Join(", ", groups.Where(g => DataProfile.StatusOf(g.Key) == StatusTone.Problem).Select(g => $"{g.Key}: {f.Format(g.Value)}")) + ".")
            : pending > 0 ? new Alert($"{f.Format(pending)} em andamento", $"{Share(pending, total)} do total ainda não foi concluído.") : null;
        var panel = new Panel(p.NameOf(status) == "Situação" ? "Situação geral" : $"{Capital(p.NameOf(status))} | situação geral",
            [new(f.Format(total, false), "total"), new(f.Format(done, false), "concluído", Tone.Green), new(f.Format(pending, false), "andamento", Tone.Orange)],
            done > 0 ? done / total : null, done > 0 ? $"{done / total:0%} concluído" : null, done > 0 ? $"{1 - done / total:0%} restante" : null, alert);
        var conclusion = done > 0
            ? $"{Share(done, total)} está concluído; {groups[0].Key} é a situação mais comum ({Share(groups[0].Value, total)})."
            : $"{groups[0].Key} é a situação mais comum, com {Share(groups[0].Value, total)} do total.";
        return new ChartSlide(p.NameOf(status) == "Situação" ? "Situação" : $"Situação | {Lower(p.NameOf(status))}", $"Por {Lower(p.NameOf(status))}", bars, false, panel, conclusion);
    }

    // ---- Rate (part of the whole) ------------------------------------------------------------------

    /// <summary>"Portas ocupadas" over "Quantidade portas" by city: the occupancy, the fullest and the emptiest.</summary>
    private static ChartSlide RateSlide(DataProfile p, DataColumn dimension, List<int> rows)
    {
        var part = p.Part!;
        var whole = p.Measure!;
        var groups = rows.GroupBy(r => Key(p, dimension, r), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Part: g.Sum(r => part.Numbers[r] ?? 0), Whole: g.Sum(r => whole.Numbers[r] ?? 0)))
            .Where(g => g.Whole > 0).OrderByDescending(g => g.Whole).ToList();
        // Extremes among groups big enough to mean something (2 of 16 is not a trend), and always on the chart.
        var wholeTotal = groups.Sum(g => g.Whole);
        var meaningful = groups.Where(g => g.Whole >= wholeTotal * 0.02 && g.Key != Missing).ToList();
        if (meaningful.Count < 2)
            meaningful = groups;
        var fullest = meaningful.MaxBy(g => g.Part / g.Whole);
        var emptiest = meaningful.MinBy(g => g.Part / g.Whole);
        var chosen = groups.Take(MaxBars).ToList();
        if (!chosen.Contains(fullest))
            chosen[^1] = fullest;
        if (!chosen.Contains(emptiest))
            chosen[chosen.Count >= 2 && chosen[^1] == fullest ? chosen.Count - 2 : chosen.Count - 1] = emptiest;
        var shown = chosen.Distinct().OrderByDescending(g => g.Part / g.Whole).ToList();
        var bars = shown.Select(g => new BarItem(g.Key, g.Part / g.Whole, $"{g.Part / g.Whole:0%} · {g.Part.ToString("N0", Br)} de {g.Whole.ToString("N0", Br)}".Replace(".", ".", StringComparison.Ordinal))).ToList();
        var rate = groups.Sum(g => g.Part) / groups.Sum(g => g.Whole);
        var what = Lower(part.Name);
        var saturating = groups.Where(g => g.Part / g.Whole >= 0.85).ToList();
        var occupancy = part.Has("OCUPADAS", "OCUPADOS", "OCUPADA", "USADAS", "USADOS");
        var alert = occupancy && saturating.Count > 0
            ? new Alert($"{saturating.Count} acima de 85%", "Perto de esgotar: " + string.Join(", ", saturating.OrderByDescending(g => g.Part / g.Whole).Take(4).Select(g => $"{g.Key} ({g.Part / g.Whole:0%})")) + (saturating.Count > 4 ? "…" : "."))
            : new Alert($"Menor: {emptiest.Key}", $"{emptiest.Part / emptiest.Whole:0%} {(occupancy ? "de ocupação" : "do total")}, contra {rate:0%} no geral.");
        var panel = new Panel($"{Capital(what)} | geral",
            [new($"{rate:0%}", "no geral"), new($"{fullest.Part / fullest.Whole:0%}", fullest.Key, Tone.Orange), new($"{emptiest.Part / emptiest.Whole:0%}", emptiest.Key, Tone.Green)],
            rate, $"{rate:0%} {(occupancy ? "ocupado" : "do total")}", $"{1 - rate:0%} {(occupancy ? "livre" : "restante")}", alert);
        var amounts = $"{groups.Sum(g => g.Part).ToString("N0", Br)} de {groups.Sum(g => g.Whole).ToString("N0", Br)}{(p.Format.Noun.Length > 0 ? " " + p.Format.Noun : string.Empty)}";
        var conclusion = occupancy
            ? $"Ocupação de {rate:0%} ({amounts}); maior em {fullest.Key} ({fullest.Part / fullest.Whole:0%}) e menor em {emptiest.Key} ({emptiest.Part / emptiest.Whole:0%})."
            : $"{Capital(what)}: {rate:0%} do total ({amounts}); maior em {fullest.Key} ({fullest.Part / fullest.Whole:0%}) e menor em {emptiest.Key} ({emptiest.Part / emptiest.Whole:0%}).";
        var caption = groups.Count > shown.Count ? $"{(occupancy ? "Ocupação" : Capital(what))} · {shown.Count} de {groups.Count} {Plural(p.NameOf(dimension), groups.Count)}" : $"{(occupancy ? "Ocupação" : Capital(what))} por {Lower(p.NameOf(dimension))}";
        return new ChartSlide($"{(occupancy ? "Ocupação" : Capital(what))} por {Lower(p.NameOf(dimension))}", caption, bars, false, panel, conclusion, Max: 1);
    }

    /// <summary>"Relatório de viabilidade_18_11_2026_16_23_14" → "Relatório de viabilidade".</summary>
    internal static string CleanName(string name)
    {
        var text = name.Replace('_', ' ');
        text = Regex.Replace(text, @"\s*\(\d+\)\s*$", string.Empty);
        text = Regex.Replace(text, @"(\s+\d{1,4}){2,}\s*$", string.Empty);
        text = Regex.Replace(text, @"\s+\d{4}-\d{2}-\d{2}.*$", string.Empty);
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '.');
        return Capital(text.Length == 0 ? name : text);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private const string Missing = "(sem informação)";

    private static string Key(DataProfile p, DataColumn column, int row) => p.Text(column, row) ?? Missing;

    /// <summary>Sum per value, largest first; values written with different case are one.</summary>
    private static List<KeyValuePair<string, double>> Group(DataProfile p, DataColumn column, List<int> rows) =>
        [.. rows.GroupBy(r => Key(p, column, r), StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, double>(g.Key, g.Sum(r => p.Value(r)!.Value)))
            .OrderByDescending(g => g.Value)];

    private static int Distinct(DataProfile p, DataColumn column, List<int> rows) =>
        rows.Select(r => p.Text(column, r)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static (DateTime First, DateTime Last)? DateSpan(DataProfile p, List<int> rows)
    {
        if (p.Date is not { } column)
            return null;
        var dates = rows.Select(r => column.Dates[r]).OfType<DateTime>().ToList();
        return dates.Count == 0 ? null : (dates.Min(), dates.Max());
    }

    private static string Share(double part, double total) => total == 0 ? "0%" : (part / total).ToString(part / total < 0.1 ? "0.#%" : "0%", Br);

    private static string Signed(double change) => (change >= 0 ? "+" : "−") + Math.Abs(change).ToString("0.#%", Br);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Br) + (IsShouting(text) ? text[1..].ToLower(Br) : text[1..]);

    private static string Lower(string text) => IsShouting(text) || text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? text.ToLower(Br) : text;

    private static bool IsShouting(string text) => text.Count(char.IsLetter) > 2 && text.Where(char.IsLetter).All(char.IsUpper);

    /// <summary>"Cidade" → "cidades", "Vendedor" → "vendedores", for "12 cidades".</summary>
    private static string Plural(string name, int count)
    {
        var word = Lower(name);
        if (count == 1 || word.Contains(' ', StringComparison.Ordinal))
            return word;
        return word.EndsWith('r') || word.EndsWith('z') ? word + "es" : word.EndsWith('l') ? word[..^1] + "is" : word.EndsWith("ão", StringComparison.Ordinal) ? word[..^2] + "ões" : word + "s";
    }
}
