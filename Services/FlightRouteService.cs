namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using FlightTracker.Models;

/// <summary>
/// Resolves flight routes using two free, no-auth sources:
///   1. adsb.lol /api/0/routeset (primary) — takes {callsign, lat, lng} and disambiguates
///      schedules that share a callsign by the aircraft's actual position. This is the fix
///      for adsbdb returning wrong origin/destination when a callsign maps to multiple
///      schedules (e.g. repositioning or reused numbers).
///   2. adsbdb.com /v0/callsign/{cs} (fallback) — used only when adsb.lol has no result.
///
/// Caching:
///   - Positive results are cached for the session lifetime (scheduled routes don't change mid-flight).
///   - Negative results are cached for a short window so a plane that couldn't be disambiguated
///     near departure gets re-tried once it's airborne with a clearer position.
/// </summary>
public sealed class FlightRouteService : IFlightRouteService
{
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _adsbLol;
    private readonly HttpClient _adsbdb;

    // Positive entries have expiresAtUtc == null (never expire for the session).
    // Negative entries (route == null) expire after NegativeCacheTtl.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Coalesces concurrent first-poll requests for the same callsign.
    private readonly ConcurrentDictionary<string, Task<FlightRoute?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FlightRouteService(IHttpClientFactory httpClientFactory)
    {
        // adsb.lol only serves the routeset endpoint when Origin/Referer look like a browser,
        // and a descriptive UA is requested by their operators.
        _adsbLol = httpClientFactory.CreateClient("adsb.lol");
        _adsbLol.BaseAddress = new Uri("https://api.adsb.lol/api/0/");
        _adsbLol.Timeout = TimeSpan.FromSeconds(10);
        _adsbLol.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
        _adsbLol.DefaultRequestHeaders.Add("Origin", "https://adsb.lol");
        _adsbLol.DefaultRequestHeaders.Add("Referer", "https://adsb.lol/");

        _adsbdb = httpClientFactory.CreateClient("adsbdb");
        _adsbdb.BaseAddress = new Uri("https://api.adsbdb.com/v0/callsign/");
        _adsbdb.Timeout = TimeSpan.FromSeconds(10);
        _adsbdb.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
    }

    public Task<FlightRoute?> GetRouteAsync(
        string callsign,
        double? lat,
        double? lon,
        CancellationToken cancellationToken)
    {
        string key = callsign.Trim();
        if (string.IsNullOrEmpty(key))
            return Task.FromResult<FlightRoute?>(null);

        if (_cache.TryGetValue(key, out var cached) && !cached.IsExpired)
            return Task.FromResult(cached.Route);

        return _inFlight.GetOrAdd(key, k => FetchAndCacheAsync(k, lat, lon, cancellationToken));
    }

    private async Task<FlightRoute?> FetchAndCacheAsync(
        string callsign,
        double? lat,
        double? lon,
        CancellationToken cancellationToken)
    {
        try
        {
            FlightRoute? route = null;

            if (lat is double latVal && lon is double lonVal)
                route = await TryAdsbLolAsync(callsign, latVal, lonVal, cancellationToken);

            route ??= await TryAdsbdbAsync(callsign, cancellationToken);

            _cache[callsign] = route is null
                ? new CacheEntry(null, DateTime.UtcNow + NegativeCacheTtl)
                : new CacheEntry(route, null);

            return route;
        }
        catch
        {
            _cache[callsign] = new CacheEntry(null, DateTime.UtcNow + NegativeCacheTtl);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(callsign, out _);
        }
    }

    private async Task<FlightRoute?> TryAdsbLolAsync(
        string callsign,
        double lat,
        double lon,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = new { planes = new[] { new { callsign, lat, lng = lon } } };
            using HttpResponseMessage response =
                await _adsbLol.PostAsJsonAsync("routeset", body, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (stream.CanSeek && stream.Length == 0)
                return null;

            var entries = await JsonSerializer.DeserializeAsync<AdsbLolRoute[]>(
                stream, JsonOptions, cancellationToken);

            var entry = entries?.FirstOrDefault();
            var airports = entry?.Airports;
            if (entry is null || airports is null || airports.Length < 2)
                return null;

            var origin = airports[0];
            var dest = airports[1];

            return BuildRoute(
                originIcao:    origin.Icao,
                originIata:    origin.Iata,
                originName:    origin.Name,
                originCity:    origin.Location,
                originCountry: origin.CountryIso2,
                originLat:     origin.Lat,
                originLon:     origin.Lon,
                destIcao:      dest.Icao,
                destIata:      dest.Iata,
                destName:      dest.Name,
                destCity:      dest.Location,
                destCountry:   dest.CountryIso2,
                destLat:       dest.Lat,
                destLon:       dest.Lon);
        }
        catch
        {
            return null;
        }
    }

    private async Task<FlightRoute?> TryAdsbdbAsync(
        string callsign,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _adsbdb.GetAsync(callsign, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var dto = await JsonSerializer.DeserializeAsync<AdsbdbCallsignResponse>(
            stream, JsonOptions, cancellationToken);

        var detail = dto?.Response?.Flightroute;
        if (detail is null)
            return null;

        return BuildRoute(
            originIcao:    detail.Origin?.IcaoCode,
            originIata:    detail.Origin?.IataCode,
            originName:    detail.Origin?.Name,
            originCity:    detail.Origin?.Municipality,
            originCountry: detail.Origin?.CountryName,
            originLat:     detail.Origin?.Latitude,
            originLon:     detail.Origin?.Longitude,
            destIcao:      detail.Destination?.IcaoCode,
            destIata:      detail.Destination?.IataCode,
            destName:      detail.Destination?.Name,
            destCity:      detail.Destination?.Municipality,
            destCountry:   detail.Destination?.CountryName,
            destLat:       detail.Destination?.Latitude,
            destLon:       detail.Destination?.Longitude);
    }

    private static FlightRoute BuildRoute(
        string? originIcao, string? originIata, string? originName, string? originCity,
        string? originCountry, double? originLat, double? originLon,
        string? destIcao, string? destIata, string? destName, string? destCity,
        string? destCountry, double? destLat, double? destLon)
    {
        double? routeDistKm = (originLat, originLon, destLat, destLon) is (double olat, double olon, double dlat, double dlon)
            ? Haversine.DistanceKm(olat, olon, dlat, dlon)
            : null;

        return new FlightRoute(
            OriginIcao:      NullIfEmpty(originIcao),
            OriginIata:      NullIfEmpty(originIata),
            OriginName:      NullIfEmpty(originName),
            OriginCity:      NullIfEmpty(originCity),
            OriginCountry:   NullIfEmpty(originCountry),
            OriginLat:       originLat,
            OriginLon:       originLon,
            DestIcao:        NullIfEmpty(destIcao),
            DestIata:        NullIfEmpty(destIata),
            DestName:        NullIfEmpty(destName),
            DestCity:        NullIfEmpty(destCity),
            DestCountry:     NullIfEmpty(destCountry),
            DestLat:         destLat,
            DestLon:         destLon,
            RouteDistanceKm: routeDistKm
        );
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private readonly record struct CacheEntry(FlightRoute? Route, DateTime? ExpiresAtUtc)
    {
        public bool IsExpired => ExpiresAtUtc is { } t && DateTime.UtcNow >= t;
    }
}
