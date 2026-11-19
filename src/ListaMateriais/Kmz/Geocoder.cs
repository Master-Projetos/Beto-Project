using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ListaMateriais.Kmz;

/// <summary>How close to the address a found point is.</summary>
internal enum GeoPrecision
{
    /// <summary>On the house number.</summary>
    Number,

    /// <summary>On the street, but not on the number (the map does not have the numbers of that street).</summary>
    Street,

    /// <summary>Somewhere in the district.</summary>
    District,

    /// <summary>Only the city was found: too far off to be put on the map.</summary>
    City,
}

/// <summary>An address to look for. Only these parts are ever sent out: never names, documents or other columns.</summary>
internal sealed record AddressQuery(string? Street, string? Number, string? District, string City, string? State, string? PostalCode)
{
    /// <summary>Same key for the same address however it was typed (case, accents, spaces).</summary>
    public string Key => AddressText.Plain(string.Join('|', Street, Number, District, City, State)).Replace("  ", " ", StringComparison.Ordinal);

    /// <summary>"Rua X, 12 - Centro, Lavras/MG".</summary>
    public string Text
    {
        get
        {
            var street = Street is null ? null : Number is null ? Street : $"{Street}, {Number}";
            var place = string.Join(" - ", new[] { street, District }.Where(p => !string.IsNullOrWhiteSpace(p)));
            var city = State is null ? City : $"{City}/{State}";
            return place.Length == 0 ? city : $"{place}, {city}";
        }
    }
}

/// <summary>What the search gave for an address. <see cref="Found"/> false when nothing was found.</summary>
/// <param name="Label">The place found, as the map names it.</param>
internal sealed record GeoResult(bool Found, double Latitude, double Longitude, GeoPrecision Precision, string Label)
{
    public static readonly GeoResult NotFound = new(false, 0, 0, GeoPrecision.City, string.Empty);

    public static string Describe(GeoPrecision precision) => precision switch
    {
        GeoPrecision.Number => "no número",
        GeoPrecision.Street => "na rua (o mapa não tem o número)",
        GeoPrecision.District => "no bairro (rua não encontrada)",
        _ => "só a cidade",
    };
}

/// <summary>The search service refused to answer: too many requests, or blocked. Looking further is pointless.</summary>
internal sealed class GeocoderRefusedException(string message) : Exception(message);

internal interface IGeocoder
{
    /// <summary>Takes at least a second per address on the free service; <see cref="GeoResult.NotFound"/> when not found.</summary>
    /// <exception cref="HttpRequestException">The service could not be reached.</exception>
    /// <exception cref="GeocoderRefusedException">The service refuses to answer.</exception>
    Task<GeoResult> FindAsync(AddressQuery query, CancellationToken cancel);
}

/// <summary>
/// OpenStreetMap's free search (Nominatim). Its rules: one request at a time, at most one a second, an
/// identified client, and results kept on our side. In Brazil few streets have their house numbers on the
/// map, so most addresses land on the street.
/// </summary>
internal sealed class NominatimGeocoder : IGeocoder, IDisposable
{
    private static readonly Uri Search = new("https://nominatim.openstreetmap.org/search");

    // Across the whole app: one request at a time, a second apart.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _lastRequest;

    private readonly HttpClient _http;
    private readonly TimeSpan _spacing;

    /// <param name="handler">For tests; null uses the network.</param>
    /// <param name="spacing">Time between requests; tests pass zero.</param>
    public NominatimGeocoder(HttpMessageHandler? handler = null, TimeSpan? spacing = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        // The service refuses clients that do not say who they are; a generic name, nothing about the user.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ListaMateriais-Geocoder", Updater.Current.ToString()));
        _http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("pt-BR"));
        _spacing = spacing ?? TimeSpan.FromSeconds(1.1);
    }

    public void Dispose() => _http.Dispose();

    public async Task<GeoResult> FindAsync(AddressQuery query, CancellationToken cancel)
    {
        // First the street (with the number, when there is one); if the street is not on the map, the district.
        if (query.Street is not null)
        {
            var street = query.Number is { } n && char.IsAsciiDigit(n[0]) ? $"{n} {query.Street}" : query.Street;
            var found = await AskAsync([("street", street), .. Place(query)], query, cancel).ConfigureAwait(false);
            if (found.Found && found.Precision != GeoPrecision.City)
                return found;
        }
        if (query.District is not null)
        {
            var text = string.Join(", ", new[] { query.District, query.City, query.State is null ? null : AddressText.StateName(query.State), "Brasil" }
                .Where(p => p is not null));
            var found = await AskAsync([("q", text)], query, cancel).ConfigureAwait(false);
            if (found.Found)
                return found.Precision == GeoPrecision.Number || found.Precision == GeoPrecision.Street ? found with { Precision = GeoPrecision.District } : found;
        }
        return GeoResult.NotFound;
    }

    private static IEnumerable<(string Key, string Value)> Place(AddressQuery query)
    {
        yield return ("city", query.City);
        if (query.State is { } state)
            yield return ("state", AddressText.StateName(state));
        yield return ("country", "Brasil");
    }

    private async Task<GeoResult> AskAsync(IEnumerable<(string Key, string Value)> parameters, AddressQuery query, CancellationToken cancel)
    {
        IEnumerable<(string Key, string Value)> extra = [("countrycodes", "br"), ("format", "jsonv2"), ("addressdetails", "1"), ("limit", "3")];
        var all = parameters.Concat(extra);
        var uri = new UriBuilder(Search)
        {
            Query = string.Join('&', all.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")),
        }.Uri;

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(uri, cancel).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt == 0)
            {
                // Asked to slow down: wait once, then give up for this run.
                await Task.Delay(TimeSpan.FromSeconds(_spacing == TimeSpan.Zero ? 0 : 10), cancel).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                throw new GeocoderRefusedException("O serviço de mapas pediu para diminuir o ritmo das buscas. Espere alguns minutos e tente de novo; os endereços já achados ficam guardados.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new GeocoderRefusedException("O serviço de mapas recusou as buscas deste computador (acesso bloqueado). Tente mais tarde.");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return Pick(body, query);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var wait = _spacing - Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRequest));
            if (_lastRequest != 0 && wait > TimeSpan.Zero)
                await Task.Delay(wait, cancel).ConfigureAwait(false);
            try
            {
                return await _http.GetAsync(uri, cancel).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lastRequest, Stopwatch.GetTimestamp());
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The first result in the right state, with how precise it is.</summary>
    internal static GeoResult Pick(string json, AddressQuery query)
    {
        if (JsonNode.Parse(json) is not JsonArray results)
            return GeoResult.NotFound;
        foreach (var result in results.OfType<JsonObject>())
        {
            var address = result["address"] as JsonObject;
            // A street of the same name in another state is not the one.
            if (query.State is { } state && address?["ISO3166-2-lvl4"]?.GetValue<string>() is { } region
                                          && !string.Equals(region, "BR-" + state, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!double.TryParse(result["lat"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(result["lon"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                continue;
            var rank = result["place_rank"]?.GetValue<int>() ?? 0;
            var category = result["category"]?.GetValue<string>();
            var houseNumber = address?["house_number"]?.GetValue<string>();
            var precision = houseNumber is not null && query.Number is not null && rank >= 28 ? GeoPrecision.Number
                : category == "highway" || rank >= 26 ? GeoPrecision.Street
                : rank >= 17 ? GeoPrecision.District
                : GeoPrecision.City;
            var label = result["display_name"]?.GetValue<string>() ?? query.Text;
            return new GeoResult(true, latitude, longitude, precision, label);
        }
        return GeoResult.NotFound;
    }
}

/// <summary>
/// The addresses already looked up, kept in the settings folder so the same address is never asked twice
/// (the free service requires it, and it makes a second run instant). Only found addresses are kept: one
/// not found may be found after the sheet is corrected.
/// </summary>
internal static class GeoCache
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, GeoResult> Missing = [];
    private static Dictionary<string, GeoResult>? _found;
    private static string _file = Path.Combine(Config.SettingsFolder, "enderecos.json");

    /// <summary>Where the cache is kept; tests point it elsewhere, which also forgets what was loaded.</summary>
    internal static string FilePath
    {
        get => _file;
        set
        {
            lock (Sync)
            {
                _file = value;
                _found = null;
                Missing.Clear();
            }
        }
    }

    /// <summary>Null when the address was never looked up.</summary>
    public static GeoResult? Get(AddressQuery query)
    {
        lock (Sync)
        {
            var found = Load();
            return found.TryGetValue(query.Key, out var result) ? result : Missing.GetValueOrDefault(query.Key);
        }
    }

    public static void Put(AddressQuery query, GeoResult result)
    {
        lock (Sync)
        {
            if (result.Found)
                Load()[query.Key] = result;
            else
                Missing[query.Key] = result;
        }
    }

    /// <summary>Forgets the addresses not found in this session, to look for them again.</summary>
    public static void ForgetMissing()
    {
        lock (Sync)
            Missing.Clear();
    }

    public static void Save()
    {
        lock (Sync)
        {
            if (_found is null)
                return;
            var json = new JsonObject();
            foreach (var (key, result) in _found)
            {
                json[key] = new JsonObject
                {
                    ["lat"] = result.Latitude, ["lon"] = result.Longitude, ["precisao"] = result.Precision.ToString(), ["lugar"] = result.Label,
                };
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file + ".tmp", json.ToJsonString());
                File.Move(_file + ".tmp", _file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing the cache only means asking again next time.
                Trace.TraceWarning("Não foi possível guardar os endereços: {0}", e.Message);
            }
        }
    }

    private static Dictionary<string, GeoResult> Load()
    {
        if (_found is not null)
            return _found;
        _found = [];
        try
        {
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject json)
            {
                foreach (var (key, value) in json)
                {
                    if (value is JsonObject entry && Enum.TryParse<GeoPrecision>(entry["precisao"]?.GetValue<string>(), out var precision))
                        _found[key] = new GeoResult(true, entry["lat"]!.GetValue<double>(), entry["lon"]!.GetValue<double>(), precision,
                            entry["lugar"]?.GetValue<string>() ?? string.Empty);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                      or NullReferenceException or FormatException)
        {
            Trace.TraceWarning("Os endereços guardados não puderam ser lidos: {0}", e.Message);
        }
        return _found;
    }
}

/// <summary>Looks for a list of addresses one after the other, keeping each answer.</summary>
internal static class AddressLocator
{
    /// <summary>How long the free service takes for so many addresses, to warn before starting.</summary>
    public static TimeSpan Estimate(int addresses) => TimeSpan.FromSeconds(addresses * 1.6);

    /// <summary>Looks for every address not yet in the cache. Stops cleanly when cancelled; what was found stays.</summary>
    /// <param name="progress">Reports how many were done, of how many.</param>
    /// <returns>How many were looked for before stopping.</returns>
    /// <exception cref="AppException">The service could not be reached or refused to answer.</exception>
    public static async Task<int> RunAsync(IReadOnlyList<AddressQuery> queries, IGeocoder geocoder, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancel)
    {
        var pending = queries.DistinctBy(q => q.Key).Where(q => GeoCache.Get(q) is null).ToList();
        var done = 0;
        var failures = 0;
        try
        {
            foreach (var query in pending)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    GeoCache.Put(query, await geocoder.FindAsync(query, cancel).ConfigureAwait(false));
                    failures = 0;
                }
                catch (Exception e) when ((e is HttpRequestException || e is TaskCanceledException && !cancel.IsCancellationRequested) && ++failures < 3)
                {
                    // A dropped connection now and then is skipped; three in a row means there is no connection.
                    Trace.TraceWarning("Falha ao buscar um endereço: {0}", e.Message);
                }
                done++;
                progress?.Report((done, pending.Count));
                if (done % 20 == 0)
                    GeoCache.Save();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (HttpRequestException e)
        {
            throw new AppException("Não foi possível falar com o serviço de mapas (OpenStreetMap). Confira a conexão com a internet.\n\n" +
                                   $"Os endereços já achados ficam guardados.\n\nDetalhe: {e.Message}", e);
        }
        catch (GeocoderRefusedException e)
        {
            throw new AppException(e.Message, e);
        }
        catch (TaskCanceledException e)
        {
            throw new AppException("O serviço de mapas demorou demais para responder. Tente de novo em alguns minutos; " +
                                   "os endereços já achados ficam guardados.", e);
        }
        finally
        {
            GeoCache.Save();
        }
        return done;
    }
}
