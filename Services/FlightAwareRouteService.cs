namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

/// <summary>
/// Fetches filed flight routes from the FlightAware AeroAPI.
///
/// Endpoint: GET https://aeroapi.flightaware.com/aeroapi/flights/{ident}
/// Auth:     x-apikey header
/// Cost:     $0.005 per call — called at most once per unique callsign.
///
/// Only the <c>route</c> field from the first result is used; all other fields
/// are ignored.  Results (including null for unknown flights) are cached in a
/// session-lifetime <see cref="ConcurrentDictionary{TKey,TValue}"/> so the API
/// is never called twice for the same callsign.
/// </summary>
public sealed class FlightAwareRouteService : IFlightAwareRouteService
{
    private readonly FlightAwareSettings _settings;
    private readonly HttpClient _httpClient;

    private readonly ConcurrentDictionary<string, FiledRoute?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<FiledRoute?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FlightAwareRouteService(AppSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings   = settings.FlightAware;
        _httpClient = httpClientFactory.CreateClient("flightaware");
        _httpClient.BaseAddress = new Uri("https://aeroapi.flightaware.com/aeroapi/");
        _httpClient.Timeout     = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
        if (!string.IsNullOrEmpty(_settings.ApiKey))
            _httpClient.DefaultRequestHeaders.Add("x-apikey", _settings.ApiKey);
    }

    public Task<FiledRoute?> GetFiledRouteAsync(string callsign, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.ApiKey))
            return Task.FromResult<FiledRoute?>(null);

        string key = callsign.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key))
            return Task.FromResult<FiledRoute?>(null);

        if (_cache.TryGetValue(key, out var cached))
            return Task.FromResult(cached);

        return _inFlight.GetOrAdd(key, k => FetchAndCacheAsync(k, cancellationToken));
    }

    private async Task<FiledRoute?> FetchAndCacheAsync(string callsign, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"flights/{Uri.EscapeDataString(callsign)}";
            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[FlightAware] {callsign}: HTTP {(int)response.StatusCode}");
                _cache[callsign] = null;
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync<FlightAwareFlightsResponse>(
                stream, JsonOptions, cancellationToken);

            string? route = dto?.Flights?.FirstOrDefault()?.Route;
            if (string.IsNullOrWhiteSpace(route))
            {
                _cache[callsign] = null;
                return null;
            }

            var filed = new FiledRoute(callsign, route.Trim());
            Console.WriteLine($"[FlightAware] {callsign}: route = {route}");
            _cache[callsign] = filed;
            return filed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlightAware] {callsign}: {ex.Message}");
            _cache[callsign] = null;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(callsign, out _);
        }
    }
}

// ── Internal DTOs ─────────────────────────────────────────────────────────────

internal sealed class FlightAwareFlightsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("flights")]
    public FlightAwareFlightEntry[]? Flights { get; set; }
}

internal sealed class FlightAwareFlightEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public string? Route { get; set; }
}
