namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using FlightTracker.Models;

/// <summary>
/// Resolves flight routes via the adsbdb.com callsign API (no auth required).
/// Endpoint: GET https://api.adsbdb.com/v0/callsign/{callsign}
/// Results are cached for the session — scheduled routes don't change while a flight is airborne.
/// Null results are also cached so an unknown callsign is never re-queried.
/// </summary>
public sealed class FlightRouteService : IFlightRouteService
{
    private readonly HttpClient _httpClient;

    // Session-lifetime cache keyed by callsign (trimmed, upper-cased).
    // Stores FlightRoute? — null means "fetched but adsbdb had no route".
    private readonly ConcurrentDictionary<string, FlightRoute?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Coalesces concurrent first-poll requests for the same callsign into one HTTP call.
    private readonly ConcurrentDictionary<string, Task<FlightRoute?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FlightRouteService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("adsbdb");
        _httpClient.BaseAddress = new Uri("https://api.adsbdb.com/v0/callsign/");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        // adsbdb asks for a descriptive User-Agent
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
    }

    public Task<FlightRoute?> GetRouteAsync(string callsign, CancellationToken cancellationToken)
    {
        string key = callsign.Trim();
        if (string.IsNullOrEmpty(key))
            return Task.FromResult<FlightRoute?>(null);

        // Hot path: already in cache
        if (_cache.TryGetValue(key, out var cached))
            return Task.FromResult(cached);

        // Coalesce concurrent callers for the same callsign onto a single Task
        return _inFlight.GetOrAdd(key, k => FetchAndCacheAsync(k, cancellationToken));
    }

    private async Task<FlightRoute?> FetchAndCacheAsync(string callsign, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(callsign, cancellationToken);

            // 404 = unknown callsign; other failures degrade silently
            if (!response.IsSuccessStatusCode)
            {
                _cache[callsign] = null;
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            // The "response" field is either a string ("invalid callsign: …") or an object.
            // We attempt to deserialise as the object form; string form means no route.
            var dto = await JsonSerializer.DeserializeAsync<AdsbdbCallsignResponse>(
                stream, JsonOptions, cancellationToken);

            var detail = dto?.Response?.Flightroute;
            if (detail is null)
            {
                _cache[callsign] = null;
                return null;
            }

            var route = new FlightRoute(
                OriginIcao: NullIfEmpty(detail.Origin?.IcaoCode),
                OriginIata: NullIfEmpty(detail.Origin?.IataCode),
                OriginName: NullIfEmpty(detail.Origin?.Name),
                DestIcao:   NullIfEmpty(detail.Destination?.IcaoCode),
                DestIata:   NullIfEmpty(detail.Destination?.IataCode),
                DestName:   NullIfEmpty(detail.Destination?.Name)
            );

            _cache[callsign] = route;
            return route;
        }
        catch
        {
            // On network error, cache null to avoid hammering the API on every poll
            _cache[callsign] = null;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(callsign, out _);
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
