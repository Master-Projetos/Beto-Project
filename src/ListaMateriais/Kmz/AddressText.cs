using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ListaMateriais.Kmz;

/// <summary>An address split into its parts. Every part may be missing.</summary>
/// <param name="Type">Kind of street, written out: "Rua", "Avenida", "Rodovia"...</param>
/// <param name="Street">Name of the street, without its type.</param>
/// <param name="Number">House number as written ("138", "12A"), "S/N", or "km 100,5".</param>
internal sealed record ParsedAddress(string? Type, string? Street, string? Number, string? Complement, string? District, string? PostalCode)
{
    public static readonly ParsedAddress Empty = new(null, null, null, null, null, null);

    /// <summary>"Rua Itapetininga".</summary>
    public string? FullStreet => Street is null ? Type : Type is null ? Street : $"{Type} {Street}";

    /// <summary>A number that can be looked up: not "S/N" nor a kilometer.</summary>
    public string? HouseNumber => Number is { } n && char.IsAsciiDigit(n[0]) ? n : null;
}

/// <summary>
/// Reads Brazilian addresses as people type them in spreadsheets: "RUA X, Nº 138", "R. X 138 - Centro",
/// "AV X, 790, apto 3", "ROD BR381 KM 100,5", "ESTRADA X, S/N", or a street name without its type.
/// Also knows the street types, the states and their codes.
/// </summary>
internal static partial class AddressText
{
    /// <summary>Street types as written in addresses (without accents, upper case), and how to write them out.</summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["RUA"] = "Rua", ["R"] = "Rua",
        ["AVENIDA"] = "Avenida", ["AV"] = "Avenida", ["AVN"] = "Avenida", ["AVDA"] = "Avenida",
        ["ALAMEDA"] = "Alameda", ["AL"] = "Alameda",
        ["TRAVESSA"] = "Travessa", ["TV"] = "Travessa", ["TRAV"] = "Travessa", ["TR"] = "Travessa",
        ["PRACA"] = "Praça", ["PC"] = "Praça", ["PCA"] = "Praça", ["PR"] = "Praça",
        ["RODOVIA"] = "Rodovia", ["ROD"] = "Rodovia",
        ["ESTRADA"] = "Estrada", ["EST"] = "Estrada", ["ESTR"] = "Estrada",
        ["LARGO"] = "Largo", ["LGO"] = "Largo", ["LG"] = "Largo",
        ["VIELA"] = "Viela", ["BECO"] = "Beco", ["VIA"] = "Via", ["VIADUTO"] = "Viaduto",
        ["VILA"] = "Vila", ["VL"] = "Vila",
        ["CHACARA"] = "Chácara", ["CH"] = "Chácara", ["FAZENDA"] = "Fazenda", ["FAZ"] = "Fazenda",
        ["SITIO"] = "Sítio", ["CONDOMINIO"] = "Condomínio", ["COND"] = "Condomínio",
        ["LOTEAMENTO"] = "Loteamento", ["LOT"] = "Loteamento",
        ["PARQUE"] = "Parque", ["PQ"] = "Parque", ["PASSAGEM"] = "Passagem", ["LADEIRA"] = "Ladeira", ["LD"] = "Ladeira",
        ["QUADRA"] = "Quadra", ["QD"] = "Quadra", ["SETOR"] = "Setor", ["CONJUNTO"] = "Conjunto",
        ["ANEL"] = "Anel", ["CAMINHO"] = "Caminho", ["MARGINAL"] = "Marginal",
    };

    private static readonly (string Code, string Name)[] StateList =
    [
        ("AC", "Acre"), ("AL", "Alagoas"), ("AP", "Amapá"), ("AM", "Amazonas"), ("BA", "Bahia"), ("CE", "Ceará"),
        ("DF", "Distrito Federal"), ("ES", "Espírito Santo"), ("GO", "Goiás"), ("MA", "Maranhão"), ("MT", "Mato Grosso"),
        ("MS", "Mato Grosso do Sul"), ("MG", "Minas Gerais"), ("PA", "Pará"), ("PB", "Paraíba"), ("PR", "Paraná"),
        ("PE", "Pernambuco"), ("PI", "Piauí"), ("RJ", "Rio de Janeiro"), ("RN", "Rio Grande do Norte"),
        ("RS", "Rio Grande do Sul"), ("RO", "Rondônia"), ("RR", "Roraima"), ("SC", "Santa Catarina"), ("SP", "São Paulo"),
        ("SE", "Sergipe"), ("TO", "Tocantins"),
    ];

    private static readonly Dictionary<string, string> StateCodes = StateList
        .SelectMany(s => new[] { (Key: s.Code, s.Code), (Key: Plain(s.Name), s.Code) })
        .ToDictionary(s => s.Key, s => s.Code, StringComparer.Ordinal);

    public static IReadOnlyList<string> States { get; } = [.. StateList.Select(s => s.Code)];

    /// <summary>"MG" for "MG", "mg", "Minas Gerais" or "MINAS GERAIS"; null otherwise.</summary>
    public static string? StateCode(string? text) =>
        text is null ? null : StateCodes.GetValueOrDefault(Plain(text));

    public static string StateName(string code) => StateList.First(s => s.Code == code).Name;

    /// <summary>"Avenida" for "AV", "Av.", "avenida"; null when the word is not a street type.</summary>
    public static string? StreetType(string? word) =>
        word is null ? null : Types.GetValueOrDefault(Plain(word).TrimEnd('.'));

    /// <summary>"CAMPINAS / SP", "Lavras - MG", "Lavras/MG" → ("CAMPINAS", "SP"); a city alone keeps a null state.</summary>
    public static (string City, string? State) SplitCity(string text)
    {
        var match = CityAndState().Match(text);
        if (match.Success && StateCode(match.Groups["uf"].Value) is { } code)
            return (match.Groups["city"].Value.Trim(), code);
        return (text.Trim(), null);
    }

    /// <summary>A CEP of 8 digits, as "37200-000"; Excel drops the leading zero of a CEP stored as a number, so 7 digits get it back.</summary>
    public static string? PostalCode(string? text)
    {
        if (text is null)
            return null;
        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        if (digits.Length == 7 && text.Trim().All(c => char.IsAsciiDigit(c)))
            digits = "0" + digits;
        return digits.Length == 8 && text.Trim().Length <= 10 ? $"{digits[..5]}-{digits[5..]}" : null;
    }

    /// <summary>"S/N" for the ways of writing "no number"; the number as written otherwise; null when it is not a house number.</summary>
    public static string? HouseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var plain = Plain(text);
        if (NoNumber().IsMatch(plain))
            return "S/N";
        var match = NumberCell().Match(plain);
        return match.Success ? match.Groups["n"].Value : null;
    }

    public static ParsedAddress Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ParsedAddress.Empty;
        var rest = Spaces().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim();

        string? postal = null;
        if (Cep().Match(rest) is { Success: true } cep)
        {
            postal = $"{cep.Groups[1].Value}-{cep.Groups[2].Value}";
            rest = Cleanup(rest.Remove(cep.Index, cep.Length));
        }

        // "Rua X, 12 - Centro - Lavras/MG": what follows the first " - " is the district, unless it is a complement or a city.
        string? district = null;
        string? complement = null;
        var parts = rest.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            rest = parts[0];
            foreach (var part in parts.Skip(1))
            {
                if (ComplementStart().IsMatch(part))
                    complement ??= part;
                else if (district is null && StateCode(part) is null && SplitCity(part).State is null)
                    district = part;
            }
        }

        if (ComplementTail().Match(rest) is { Success: true } tail && tail.Index > 0)
        {
            complement ??= Cleanup(tail.Value);
            rest = Cleanup(rest[..tail.Index]);
        }

        // "ROD BR381" → "BR-381", the way the highways are named on the map (and so its digits are not taken for the number).
        rest = Highway().Replace(rest, m => $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}");

        string? number = null;
        foreach (var pattern in new[] { Kilometer(), NoNumberTail(), MarkedNumber(), TrailingNumber() })
        {
            var match = pattern.Match(rest);
            if (!match.Success)
                continue;
            var before = Cleanup(rest[..match.Index]);
            // "Rua 7" is the name of the street, not number 7 of a street with no name.
            if (pattern == TrailingNumber() && (before.Length == 0 || StreetType(before) is not null && !before.Contains(' ', StringComparison.Ordinal)))
                continue;
            number = match.Groups["km"].Success ? "km " + match.Groups["km"].Value
                : match.Groups["n"].Success ? match.Groups["n"].Value.ToUpperInvariant()
                : "S/N";
            rest = before;
            break;
        }

        string? type = null;
        var words = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 2 && StreetType(words[0]) is { } written)
        {
            type = written;
            rest = words[1];
        }
        else if (words.Length == 1 && StreetType(words[0]) is { } alone)
        {
            type = alone;
            rest = string.Empty;
        }
        var street = Cleanup(rest);
        return new ParsedAddress(type, street.Length == 0 ? null : street, number, complement, district, postal);
    }

    /// <summary>Upper case without accents, for comparing what people type.</summary>
    public static string Plain(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToUpperInvariant(c));
        }
        return result.ToString();
    }

    private static string Cleanup(string text) => text.Trim().Trim(',', ';', '-', '.', ' ').Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(\d{5})-?(\d{3})\b")]
    private static partial Regex Cep();

    [GeneratedRegex(@"^(?<city>.+?)\s*[/\-–(]\s*(?<uf>[A-Za-z]{2})\s*\)?\s*$")]
    private static partial Regex CityAndState();

    [GeneratedRegex(@"^(?:S\s*/?\s*N[ºO°.]?|S\.\s*N\.?|SEM\s+NUMERO|SN)$")]
    private static partial Regex NoNumber();

    [GeneratedRegex(@"^(?:N[ºO°.]?\s*)?(?<n>\d{1,6}\s?[A-Z]?)$")]
    private static partial Regex NumberCell();

    [GeneratedRegex(@"(?i)(?:,\s*|\s+)\bKM\.?\s*(?<km>\d+(?:[.,]\d+)?)\b.*$")]
    private static partial Regex Kilometer();

    [GeneratedRegex(@"(?i)(?:,\s*|\s+)(?:S\s*/\s*N[º°o.]?|S\.\s*N\.?|SEM\s+N[UÚ]MERO)\s*$")]
    private static partial Regex NoNumberTail();

    [GeneratedRegex(@"(?i)(?:,\s*|\s+)(?:N\s*[º°o.]+|N(?=\s*\d)|N[UÚ]M(?:ERO)?\.?|NRO\.?)\s*(?<n>\d{1,6}\s?[A-Za-z]?)\b\s*$")]
    private static partial Regex MarkedNumber();

    [GeneratedRegex(@"(?:,\s*|\s+)(?<n>\d{1,6}[A-Za-z]?)\s*$")]
    private static partial Regex TrailingNumber();

    /// <summary>Words that start a complement: apartment, room, block, lot...</summary>
    [GeneratedRegex(@"(?i)^(?:APTO?\.?|APARTAMENTO|AP\.|CASA|SALA|SL\.?|BLOCO|BL\.?|LOTE|LT\.?|LOJA|LJ\.?|FUNDOS|FDS|ANDAR|GALP[AÃ]O|BOX|CONJ\.?)\b")]
    private static partial Regex ComplementStart();

    /// <summary>A complement after a comma or right after the number: "…, Sala 1", "… 77 APTO 12" (but not "Rua Casa Branca").</summary>
    [GeneratedRegex(@"(?i)(?:,\s*|(?<=\d[A-Za-z]?)\s+)(?:APTO?|APARTAMENTO|AP|CASA|SALA|SL|BLOCO|BL|LOTE|LT|LOJA|LJ|FUNDOS|FDS|ANDAR|GALP[AÃ]O|BOX)\.?(?=\s|\d|$).*$")]
    private static partial Regex ComplementTail();

    [GeneratedRegex(@"(?i)\b(BR|MG|SP|RJ|ES|GO|BA|PR|SC|RS|MS|MT|DF|AMG|LMG)\s*-?\s*(\d{2,3})\b")]
    private static partial Regex Highway();
}
