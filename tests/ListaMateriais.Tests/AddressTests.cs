using System.Net;
using ListaMateriais.Kmz;

namespace ListaMateriais.Tests;

// Every address here is made up; they only copy the shapes people type.
public class AddressTextTests
{
    [Theory]
    [InlineData("RUA DAS FLORES, Nº 138", "Rua", "DAS FLORES", "138", null)]
    [InlineData("RUA DAS FLORES, N°10", "Rua", "DAS FLORES", "10", null)]
    [InlineData("RUA PEDRO ALVES N48", "Rua", "PEDRO ALVES", "48", null)]
    [InlineData("AV BRASIL 790", "Avenida", "BRASIL", "790", null)]
    [InlineData("Av. Brasil, 790, apto 3", "Avenida", "Brasil", "790", "apto 3")]
    [InlineData("R. Sete de Setembro 100 - Centro", "Rua", "Sete de Setembro", "100", null)]
    [InlineData("Rua 7 de Setembro, 12", "Rua", "7 de Setembro", "12", null)]
    [InlineData("Rua 7", "Rua", "7", null, null)]
    [InlineData("ESTRADA DO SITIO, S/N", "Estrada", "DO SITIO", "S/N", null)]
    [InlineData("RODOVIA BR 040, S/N°", "Rodovia", "BR-040", "S/N", null)]
    [InlineData("ROD BR381 KM 100,5", "Rodovia", "BR-381", "km 100,5", null)]
    [InlineData("QUINZE DE NOVEMBRO, Nº 50", null, "QUINZE DE NOVEMBRO", "50", null)]
    [InlineData("CHACARA BOA VISTA", "Chácara", "BOA VISTA", null, null)]
    [InlineData("RUA DOS IPES Nº 77 APTO 12", "Rua", "DOS IPES", "77", "APTO 12")]
    [InlineData("Rua das Acácias 45, Sala 2", "Rua", "das Acácias", "45", "Sala 2")]
    [InlineData("Rua Casa Branca, 15", "Rua", "Casa Branca", "15", null)]
    public void Splits_an_address_typed_in_one_cell(string text, string? type, string? street, string? number, string? complement)
    {
        var address = AddressText.Parse(text);

        Assert.Equal(type, address.Type);
        Assert.Equal(street, address.Street);
        Assert.Equal(number, address.Number);
        Assert.Equal(complement, address.Complement);
    }

    [Fact]
    public void Finds_the_district_and_the_postal_code()
    {
        var address = AddressText.Parse("Rua das Flores, 82 - Jardim Primavera - 37200-000");

        Assert.Equal("Jardim Primavera", address.District);
        Assert.Equal("37200-000", address.PostalCode);
        Assert.Equal("82", address.HouseNumber);
        Assert.Null(AddressText.Parse("ESTRADA DO SITIO, S/N").HouseNumber);
    }

    [Theory]
    [InlineData("CAMPINAS / SP", "CAMPINAS", "SP")]
    [InlineData("Lavras - MG", "Lavras", "MG")]
    [InlineData("Lavras/mg", "Lavras", "MG")]
    [InlineData("São João del-Rei", "São João del-Rei", null)]
    [InlineData("Divinópolis", "Divinópolis", null)]
    public void Splits_a_city_written_with_its_state(string text, string city, string? state) =>
        Assert.Equal((city, state), AddressText.SplitCity(text));

    [Fact]
    public void Knows_states_postal_codes_and_numbers()
    {
        Assert.Equal("MG", AddressText.StateCode("Minas Gerais"));
        Assert.Equal("SP", AddressText.StateCode("sp"));
        Assert.Null(AddressText.StateCode("BIL"));
        Assert.Equal("01310-100", AddressText.PostalCode("1310100"));
        Assert.Equal("37200-000", AddressText.PostalCode("37200000"));
        Assert.Null(AddressText.PostalCode("123"));
        Assert.Equal("S/N", AddressText.HouseNumber("s/nº"));
        Assert.Equal("12A", AddressText.HouseNumber("12A"));
        Assert.Null(AddressText.HouseNumber("009999/2"));
    }
}

public class ColumnGuesserTests
{
    private static SheetTable Table(params string?[][] rows) =>
        new("Dados", rows, [.. Enumerable.Range(1, rows.Length)], rows.Max(r => r.Length));

    [Fact]
    public void A_sheet_with_the_address_split_in_columns()
    {
        var table = Table(
            ["id", "cpfCnpj", "tipo", "nome", "cep", "endereco", "numero", "bairro", "cidade", "estado", "lat", "long"],
            ["1", "11111111111", "F", "FULANO DE TAL", "37200100", "RUA DAS FLORES", "101", "VILA NOVA", "LAVRAS", "MG", null, null],
            ["2", "22222222222", "J", "EMPRESA EXEMPLO LTDA", "35500100", "RUA DO COMERCIO", "202", "CENTRO", "DIVINOPOLIS", "MG", null, null],
            ["3", "33333333333", "F", "BELTRANO DA SILVA", "37010000", "AVENIDA PRINCIPAL", "20", "JARDIM AMERICA", "VARGINHA", "MG", "-21.55", "-45.43"]);

        var columns = ColumnGuesser.Guess(table, hasHeader: true);

        Assert.Equal(new SheetColumns(Name: 3, Latitude: 10, Longitude: 11, Street: 5, Number: 6, District: 7, City: 8, State: 9, PostalCode: 4),
            columns);
    }

    [Fact]
    public void A_sheet_with_the_whole_address_in_one_cell_and_traps_around_it()
    {
        // "TIPO" holds TORRE/POP (not a street type) and "Nº CONTRATO" is not the house number.
        var table = Table(
            ["STATUS", "Nº CONTRATO", "NOME DA TORRE", "LOCADOR", "ENDEREÇO", "BAIRRO", "CIDADE", "UF", "TIPO"],
            ["ATIVO", "001234", "TORRE NORTE", "JOSE EXEMPLO", "RUA DAS FLORES 15", "VALE VERDE", "CIDADE NORTE", "MG", "TORRE"],
            ["ATIVO", "009999/2", "POP SUL", "MARIA EXEMPLO", "RUA SESSENTA, Nº 31", "JARDIM AZUL", "CIDADE SUL", "MG", "POP"],
            ["RETIRADA", "002000", "TORRE LESTE", "EMPRESA X", "AV BRASIL, Nº 10", "CENTRO", "CIDADE LESTE", "MG", "TORRE"]);

        var columns = ColumnGuesser.Guess(table, hasHeader: true);

        Assert.Equal(2, columns.Name);
        Assert.Equal(4, columns.Street);
        Assert.Equal(-1, columns.Number);
        Assert.Equal(-1, columns.StreetType);
        Assert.Equal((5, 6, 7), (columns.District, columns.City, columns.State));
        Assert.False(columns.HasCoordinates);
    }

    [Fact]
    public void Coordinates_with_a_three_letter_code_that_is_not_a_state()
    {
        var table = Table(
            ["UF", "CIDADE", "SIGLA MASTER", "DESIGNAÇÃO", "LAT", "LONG", "APELIDO DO POP", "Nº INST. CONCESSIONÁRIA", "ENDEREÇO", "BAIRRO"],
            ["SP", "CAMPINAS", "ABC", "SP-CPS-TRE-ABC", "-22.90", " -47.06", "ALFA", "123456", "RUA UM, Nº 1", "CENTRO"],
            ["MG", "LAVRAS", "DEF", "MG-LAV-TRE-DEF", "-21.24", "-45.00", "BETA", "654321", "RUA DOIS 20", "CENTRO"]);

        var columns = ColumnGuesser.Guess(table, hasHeader: true);

        Assert.Equal((0, 1), (columns.State, columns.City));
        Assert.Equal((4, 5), (columns.Latitude, columns.Longitude));
        Assert.Equal(6, columns.Name);
        Assert.Equal(-1, columns.Number);
        Assert.Equal(8, columns.Street);
    }

    [Fact]
    public void The_street_type_in_a_column_of_its_own()
    {
        var table = Table(
            ["Cliente", "Tipo", "Logradouro", "Nº", "Cidade"],
            ["A", "R", "das Flores", "10", "Lavras/MG"],
            ["B", "Av.", "Brasil", "s/n", "Lavras/MG"],
            ["C", "Rua", "Sete", "33", "Lavras/MG"]);

        var columns = ColumnGuesser.Guess(table, hasHeader: true);

        Assert.Equal(new SheetColumns(Name: 0, StreetType: 1, Street: 2, Number: 3, City: 4), columns);

        var query = SheetPoints.Query(table.Rows[2], columns, AddressDefaults.None, out _);
        Assert.Equal(new AddressQuery("Avenida Brasil", "S/N", null, "Lavras", "MG", null), query);
    }
}

public class SheetAddressTests
{
    [Fact]
    public void Rows_with_coordinates_keep_them_and_the_others_wait_for_their_address()
    {
        var table = new SheetTable("Torres",
        [
            ["NOME", "ENDEREÇO", "BAIRRO", "LAT", "LONG"],
            ["A", "RUA UM, Nº 1", "CENTRO", "-21.24", "-45.00"],
            ["B", "RUA DOIS 20", "CENTRO", null, null],
            ["C", "RUA DOIS 20", "CENTRO", null, null],
            ["D", "RUA TRES, Nº 3", "CENTRO", null, null],
            ["E", null, null, null, null],
        ], [1, 2, 3, 4, 5, 6], 5);
        var columns = ColumnGuesser.Guess(table, hasHeader: true);

        var first = SheetPoints.Read(table, columns, hasHeader: true, new AddressDefaults("Lavras", "MG"), _ => null);

        Assert.Equal(1, first.FromCoordinates);
        Assert.Equal("A", Assert.Single(first.Points.Points).Name);
        // The same address on two rows is looked up once.
        Assert.Equal(["Rua DOIS, 20 - CENTRO, Lavras/MG", "Rua TRES, 3 - CENTRO, Lavras/MG"], first.Pending.Select(q => q.Text));
        Assert.Contains("sem coordenadas nem endereço", Assert.Single(first.Points.Problems), StringComparison.Ordinal);

        var found = SheetPoints.Read(table, columns, hasHeader: true, new AddressDefaults("Lavras", "MG"), query =>
            query.Street == "Rua DOIS" ? new GeoResult(true, -21.25, -45.01, GeoPrecision.Street, "Rua Dois, Centro, Lavras")
                : new GeoResult(true, -21.2, -45.0, GeoPrecision.City, "Lavras"));

        Assert.Empty(found.Pending);
        Assert.Equal(["A", "B", "C"], found.Points.Points.Select(p => p.Name));
        Assert.Contains("na rua", found.Points.Points[1].Description, StringComparison.Ordinal);
        Assert.Equal(2, found.Located[GeoPrecision.Street]);
        // Only the city: left out instead of piling on the city center.
        Assert.Contains(found.Points.Problems, p => p.StartsWith("Linha 5", StringComparison.Ordinal) && p.Contains("só a cidade", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_city_the_row_says_so()
    {
        var columns = new SheetColumns(Street: 0);
        Assert.Null(SheetPoints.Query(["RUA UM, 1"], columns, AddressDefaults.None, out var problem));
        Assert.Contains("sem cidade", problem, StringComparison.Ordinal);
        Assert.NotNull(SheetPoints.Query(["RUA UM, 1"], columns, new AddressDefaults("Lavras", "Minas Gerais"), out _));
    }
}

public class GeocoderTests
{
    private const string Street = """
        [{"lat":"-21.2450","lon":"-45.0010","category":"highway","type":"residential","place_rank":26,
          "display_name":"Rua Um, Centro, Lavras, Minas Gerais, Brasil","address":{"road":"Rua Um","ISO3166-2-lvl4":"BR-MG"}}]
        """;

    [Fact]
    public void Reads_how_precise_each_answer_is()
    {
        var query = new AddressQuery("Rua Um", "10", null, "Lavras", "MG", null);
        var number = NominatimGeocoder.Pick("""
            [{"lat":"-21.1","lon":"-45.1","category":"place","type":"house","place_rank":30,"display_name":"10, Rua Um",
              "address":{"house_number":"10","ISO3166-2-lvl4":"BR-MG"}}]
            """, query);
        Assert.Equal((true, GeoPrecision.Number, -21.1), (number.Found, number.Precision, number.Latitude));
        Assert.Equal(GeoPrecision.Street, NominatimGeocoder.Pick(Street, query).Precision);
        // A street of the same name in another state is not taken.
        Assert.False(NominatimGeocoder.Pick(Street.Replace("BR-MG", "BR-SP", StringComparison.Ordinal), query).Found);
        Assert.False(NominatimGeocoder.Pick("[]", query).Found);
    }

    [Fact]
    public async Task Sends_only_the_address_and_falls_back_to_the_district()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Query.Contains("street=", StringComparison.Ordinal)
            ? "[]"
            : """[{"lat":"-21.2","lon":"-45.0","category":"place","type":"suburb","place_rank":19,"display_name":"Centro, Lavras","address":{"ISO3166-2-lvl4":"BR-MG"}}]""");
        using var geocoder = new NominatimGeocoder(handler, TimeSpan.Zero);

        var result = await geocoder.FindAsync(new AddressQuery("Rua Inexistente", "5", "Centro", "Lavras", "MG", null), CancellationToken.None);

        Assert.Equal((true, GeoPrecision.District), (result.Found, result.Precision));
        Assert.Equal(2, handler.Requests.Count);
        var first = Uri.UnescapeDataString(handler.Requests[0].Query);
        Assert.Contains("street=5 Rua Inexistente", first, StringComparison.Ordinal);
        Assert.Contains("state=Minas Gerais", first, StringComparison.Ordinal);
        Assert.Contains("q=Centro, Lavras, Minas Gerais, Brasil", Uri.UnescapeDataString(handler.Requests[1].Query), StringComparison.Ordinal);
        Assert.StartsWith("ListaMateriais-Geocoder/", handler.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_service_stops_the_search()
    {
        using var geocoder = new NominatimGeocoder(new FakeHandler(_ => null), TimeSpan.Zero);
        await Assert.ThrowsAsync<GeocoderRefusedException>(() =>
            geocoder.FindAsync(new AddressQuery("Rua Um", null, null, "Lavras", "MG", null), CancellationToken.None));
    }

    [Fact]
    public async Task The_locator_asks_once_per_address_and_keeps_the_answers()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"geo-{Guid.NewGuid():N}");
        var previous = GeoCache.FilePath;
        GeoCache.FilePath = Path.Combine(folder, "enderecos.json");
        try
        {
            var geocoder = new CountingGeocoder();
            var a = new AddressQuery("Rua Um", "1", null, "Lavras", "MG", null);
            var b = new AddressQuery("RUA UM", "1", null, "LAVRAS", "MG", null);
            var c = new AddressQuery("Rua Dois", null, null, "Lavras", "MG", null);
            var reports = new List<(int, int)>();

            await AddressLocator.RunAsync([a, b, c], geocoder, new Progress<(int, int)>(reports.Add), CancellationToken.None);

            Assert.Equal(2, geocoder.Calls);
            Assert.True(GeoCache.Get(b)!.Found);
            Assert.False(GeoCache.Get(c)!.Found);
            // Kept on disk: found ones only, so a corrected address is looked for again.
            GeoCache.FilePath = GeoCache.FilePath;
            Assert.True(GeoCache.Get(a)!.Found);
            Assert.Null(GeoCache.Get(c));
            await AddressLocator.RunAsync([a], geocoder, null, CancellationToken.None);
            Assert.Equal(2, geocoder.Calls);
        }
        finally
        {
            GeoCache.FilePath = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class CountingGeocoder : IGeocoder
    {
        public int Calls { get; private set; }

        public Task<GeoResult> FindAsync(AddressQuery query, CancellationToken cancel)
        {
            Calls++;
            return Task.FromResult(query.Street!.EndsWith("Um", StringComparison.OrdinalIgnoreCase)
                ? new GeoResult(true, -21.24, -45.0, GeoPrecision.Street, "Rua Um")
                : GeoResult.NotFound);
        }
    }

    /// <summary>Answers each request with the JSON given; null answers 403.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            UserAgent = request.Headers.UserAgent.ToString();
            var body = answer(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
