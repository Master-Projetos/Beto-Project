using System.Globalization;
using System.Text.RegularExpressions;

namespace ListaMateriais.Kmz;

/// <summary>What a column of a points sheet holds.</summary>
internal enum ColumnRole
{
    Name,
    Latitude,
    Longitude,

    /// <summary>"Rua", "Av.", "Travessa"... in a column of its own.</summary>
    StreetType,

    /// <summary>The street, or the whole address in one cell ("RUA X, Nº 12 - Centro").</summary>
    Street,

    Number,
    Complement,
    District,
    City,
    State,
    PostalCode,
}

/// <summary>Which column holds each part, -1 when none does.</summary>
internal sealed record SheetColumns(
    int Name = -1, int Latitude = -1, int Longitude = -1, int StreetType = -1, int Street = -1, int Number = -1,
    int Complement = -1, int District = -1, int City = -1, int State = -1, int PostalCode = -1)
{
    public static readonly SheetColumns None = new();

    public bool HasCoordinates => Latitude >= 0 && Longitude >= 0;

    public bool HasAddress => Street >= 0 || District >= 0 || PostalCode >= 0;

    public int this[ColumnRole role] => role switch
    {
        ColumnRole.Name => Name,
        ColumnRole.Latitude => Latitude,
        ColumnRole.Longitude => Longitude,
        ColumnRole.StreetType => StreetType,
        ColumnRole.Street => Street,
        ColumnRole.Number => Number,
        ColumnRole.Complement => Complement,
        ColumnRole.District => District,
        ColumnRole.City => City,
        ColumnRole.State => State,
        _ => PostalCode,
    };

    public SheetColumns With(ColumnRole role, int column) => role switch
    {
        ColumnRole.Name => this with { Name = column },
        ColumnRole.Latitude => this with { Latitude = column },
        ColumnRole.Longitude => this with { Longitude = column },
        ColumnRole.StreetType => this with { StreetType = column },
        ColumnRole.Street => this with { Street = column },
        ColumnRole.Number => this with { Number = column },
        ColumnRole.Complement => this with { Complement = column },
        ColumnRole.District => this with { District = column },
        ColumnRole.City => this with { City = column },
        ColumnRole.State => this with { State = column },
        _ => this with { PostalCode = column },
    };
}

/// <summary>
/// Predicts what each column of a sheet holds, from its title and from what is written in it. The title
/// alone is not trusted: a "TIPO" column with TORRE/POP is not the street type, "Nº CONTRATO" is not the
/// house number, and a "SIGLA" of three letters is not the state. Each column gets a score for each part;
/// the best scores win, one column per part.
/// </summary>
internal static partial class ColumnGuesser
{
    private const int Sample = 300;

    /// <summary>Title words that say what a column is, from the strongest. Matched as whole words.</summary>
    private static readonly (ColumnRole Role, string[] Phrases)[] Titles =
    [
        (ColumnRole.Latitude, ["LATITUDE", "LAT", "Y"]),
        (ColumnRole.Longitude, ["LONGITUDE", "LONG", "LON", "LNG", "X"]),
        (ColumnRole.StreetType, ["TIPO LOGRADOURO", "TIPO DE LOGRADOURO", "TP LOGRADOURO", "TP LOGR", "TIPO LOGR", "TIPO VIA", "TIPO DA VIA", "TIPO RUA", "TIPO"]),
        (ColumnRole.Street, ["ENDERECO COMPLETO", "ENDERECO", "LOGRADOURO", "RUA", "ADDRESS", "STREET", "END", "LOGR", "LOCALIZACAO"]),
        (ColumnRole.Number, ["NUMERO", "NUM", "NRO", "NO", "N"]),
        (ColumnRole.Complement, ["COMPLEMENTO", "COMPL"]),
        (ColumnRole.District, ["BAIRRO", "DISTRITO", "NEIGHBORHOOD", "SETOR"]),
        (ColumnRole.City, ["CIDADE", "MUNICIPIO", "LOCALIDADE", "CITY"]),
        (ColumnRole.State, ["UF", "ESTADO", "STATE"]),
        (ColumnRole.PostalCode, ["CEP", "CODIGO POSTAL", "ZIP"]),
        // The friendly name first: the nickname of a tower reads better on the map than its designation code.
        (ColumnRole.Name, ["NOME DA TORRE", "APELIDO", "NOME", "TORRE", "CLIENTE", "RAZAO SOCIAL", "TITULO", "NAME", "DESIGNACAO",
            "IDENTIFICACAO", "ESTACAO", "SITE", "POP", "PONTO", "DESCRICAO", "LABEL", "ROTULO", "ID"]),
    ];

    /// <summary>Words that, next to a number word, mean the column is some other number (a contract, an installation...).</summary>
    private static readonly HashSet<string> NumberCompanions = new(StringComparer.Ordinal)
    {
        "DO", "DA", "DE", "ENDERECO", "IMOVEL", "RESIDENCIA", "CASA", "LOGRADOURO", "PREDIAL", "NUMERO", "NUM", "NO", "N", "NRO",
    };

    private static readonly string[] DistrictWords =
    [
        "JARDIM", "JD", "VILA", "VL", "PARQUE", "PQ", "CENTRO", "CONJUNTO", "RESIDENCIAL", "BAIRRO", "LOTEAMENTO", "SETOR", "NUCLEO",
        "ZONA", "DISTRITO", "CHACARAS", "RECANTO", "MORADA", "PORTAL",
    ];

    public static SheetColumns Guess(SheetTable table, bool hasHeader)
    {
        if (table.Rows.Count == 0)
            return SheetColumns.None;
        var header = hasHeader ? table.Rows[0] : [];
        var rows = table.Rows.Skip(hasHeader ? 1 : 0).Take(Sample).ToList();
        var roles = Enum.GetValues<ColumnRole>();
        var scores = new List<(ColumnRole Role, int Column, double Score)>();
        for (var column = 0; column < table.Columns; column++)
        {
            var title = column < header.Length && header[column] is { } text ? Words(text) : [];
            var values = rows.Select(row => column < row.Length ? row[column]?.Trim() : null)
                .Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
            if (values.Count == 0)
                continue;
            var content = new Content(values);
            foreach (var role in roles)
            {
                var score = Score(role, title, content);
                if (score > 0)
                    scores.Add((role, column, score + (title.Length == 0 ? 0 : 0.001 * (table.Columns - column))));
            }
        }

        var result = SheetColumns.None;
        var taken = new HashSet<int>();
        var done = new HashSet<ColumnRole>();
        foreach (var (role, column, _) in scores.OrderByDescending(s => s.Score))
        {
            if (done.Contains(role) || taken.Contains(column))
                continue;
            result = result.With(role, column);
            done.Add(role);
            taken.Add(column);
        }
        return result;
    }

    /// <summary>How sure the column holds that part: 0 for no; a clear title is worth about 2, content up to about 2 more.</summary>
    private static double Score(ColumnRole role, string[] title, Content content)
    {
        var named = TitleScore(role, title);
        switch (role)
        {
            case ColumnRole.Latitude:
            case ColumnRole.Longitude:
            {
                var coordinates = content.Share(v => Coordinate.TryParse(v, out var d) && Math.Abs(d) <= (role == ColumnRole.Latitude ? 90 : 180) && Math.Abs(d) >= 0.5);
                if (coordinates < 0.5)
                    return 0;
                // Without a title, a column of coordinates is taken by its range: Brazil is south and far west.
                var inBrazil = content.Share(v => Coordinate.TryParse(v, out var d)
                                                  && (role == ColumnRole.Latitude ? d is >= -34 and <= 6 : d is >= -75 and <= -28));
                return named > 0 ? named + coordinates : inBrazil > 0.8 && !content.LooksLikeIds ? 0.6 * inBrazil : 0;
            }
            case ColumnRole.StreetType:
            {
                // Only a column where most values are street types: "TIPO" with TORRE / POP is something else. Without a
                // title saying so, nearly all of them must be.
                var types = content.Share(v => AddressText.StreetType(v) is not null);
                return types >= 0.6 && (named > 0 || types >= 0.9 && content.Distinct < 0.5) ? Math.Max(named, 1) + types : 0;
            }
            case ColumnRole.Street:
            {
                var text = content.Share(v => v.Count(char.IsLetter) >= 3);
                if (text < 0.6)
                    return 0;
                var shaped = content.Share(v => AddressText.Parse(v) is { } a && (a.Type is not null || a.Number is not null));
                return named > 0 ? named + text + shaped : shaped >= 0.6 ? 1.2 * shaped : 0;
            }
            case ColumnRole.Number:
            {
                if (named == 0 || !IsNumberTitle(title))
                    return 0;
                var numbers = content.Share(v => AddressText.HouseNumber(v) is not null);
                return numbers >= 0.6 ? named + numbers : 0;
            }
            case ColumnRole.Complement:
                return named;
            case ColumnRole.District:
            {
                var looks = content.Share(v => DistrictWords.Contains(AddressText.Plain(v).Split(' ')[0]));
                return named > 0 ? named + looks : 0;
            }
            case ColumnRole.City:
            {
                if (named == 0 || content.Share(v => v.Count(char.IsLetter) >= 2) < 0.6)
                    return 0;
                // Cities repeat a lot down a sheet.
                return named + (content.Distinct < 0.5 ? 0.5 : 0);
            }
            case ColumnRole.State:
            {
                // Checked against the 27 states: "SIGLA" with three letters is not a state.
                var states = content.Share(v => AddressText.StateCode(v) is not null);
                return states >= 0.8 ? Math.Max(named, 0.8) + states : 0;
            }
            case ColumnRole.PostalCode:
            {
                var codes = content.Share(v => AddressText.PostalCode(v) is not null);
                return codes >= 0.7 ? (named > 0 ? named + codes : 0.5 * codes) : 0;
            }
            default:
            {
                if (named == 0)
                    return 0;
                var text = content.Share(v => v.Any(char.IsLetter));
                // A name tells the points apart: mostly different values.
                return named * (0.5 + 0.5 * text) + content.Distinct;
            }
        }
    }

    /// <summary>
    /// 2 when the title is one of the phrases for that part; a bit less for later (weaker) phrases;
    /// 0 when no phrase is in the title.
    /// </summary>
    private static double TitleScore(ColumnRole role, string[] title)
    {
        if (title.Length == 0)
            return 0;
        var phrases = Titles.First(t => t.Role == role).Phrases;
        for (var i = 0; i < phrases.Length; i++)
        {
            var words = phrases[i].Split(' ');
            if (Contains(title, words))
                return (words.Length == title.Length ? 2.2 : 2.1) - 0.05 * i;
        }
        return 0;
    }

    /// <summary>"Número", "Nº", "Num. endereço"; not "Nº contrato" nor "Nº inst. concessionária".</summary>
    private static bool IsNumberTitle(string[] title) => title.All(NumberCompanions.Contains);

    private static bool Contains(string[] title, string[] words)
    {
        for (var start = 0; start + words.Length <= title.Length; start++)
        {
            if (words.Select((w, i) => title[start + i] == w).All(x => x))
                return true;
        }
        return false;
    }

    /// <summary>"Nº INST. CONCESSIONÁRIA" → ["NO", "INST", "CONCESSIONARIA"].</summary>
    internal static string[] Words(string title) =>
        WordSplit().Split(AddressText.Plain(title)).Where(w => w.Length > 0).ToArray();

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex WordSplit();

    /// <summary>A sample of the values of a column, with the shares asked about.</summary>
    private sealed class Content(List<string> values)
    {
        public double Share(Func<string, bool> test) => values.Count(test) / (double)values.Count;

        /// <summary>Distinct values over all values: near 1 for names and addresses, low for cities and states.</summary>
        public double Distinct { get; } = values.Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)values.Count;

        /// <summary>Whole numbers counting up (1, 2, 3...): an id column, not a coordinate.</summary>
        public bool LooksLikeIds { get; } = values.All(v => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
