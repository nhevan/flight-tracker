namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Fetches aircraft photos from the planespotters.net public API.
/// No API key required. Tries ICAO24 hex first, then falls back to registration.
/// Uses session-lifetime caching and request coalescing to minimise API calls.
/// </summary>
public sealed class PlaneSpottersPhotoService : IAircraftPhotoService
{
    private readonly HttpClient _httpClient;

    // Cache null too — if a lookup returned nothing, don't retry every poll
    private readonly ConcurrentDictionary<string, string?> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    // Coalesce concurrent requests for the same aircraft
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight
        = new(StringComparer.OrdinalIgnoreCase);

    public PlaneSpottersPhotoService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("planespotters");
        _httpClient.BaseAddress = new Uri("https://api.planespotters.net/pub/photos/");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
    }

    public Task<string?> GetPhotoUrlAsync(
        string icao24,
        string? registration,
        CancellationToken cancellationToken)
    {
        string key = icao24.ToUpperInvariant();

        if (_cache.TryGetValue(key, out string? cached))
            return Task.FromResult(cached);

        return _inFlight.GetOrAdd(key, _ => FetchAndCacheAsync(icao24, registration, cancellationToken));
    }

    private async Task<string?> FetchAndCacheAsync(
        string icao24,
        string? registration,
        CancellationToken cancellationToken)
    {
        string key = icao24.ToUpperInvariant();
        try
        {
            // 1. Try by ICAO24 hex
            string? url = await FetchFromEndpointAsync($"hex/{icao24}", cancellationToken);

            // 2. Fall back to registration if hex returned nothing
            if (url is null && !string.IsNullOrWhiteSpace(registration))
                url = await FetchFromEndpointAsync(
                    $"reg/{Uri.EscapeDataString(registration.Trim())}", cancellationToken);

            _cache[key] = url;
            return url;
        }
        catch
        {
            // Never let photo failure crash the tracker
            _cache[key] = null;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<string?> FetchFromEndpointAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var doc = await response.Content
            .ReadFromJsonAsync<PlaneSpottersResponse>(cancellationToken: cancellationToken);

        return doc?.Photos?.FirstOrDefault()?.ThumbnailLarge?.Src;
    }

    // ── Private DTOs ──────────────────────────────────────────────────────────

    private sealed class PlaneSpottersResponse
    {
        [JsonPropertyName("photos")]
        public List<PlaneSpottersPhoto>? Photos { get; set; }
    }

    private sealed class PlaneSpottersPhoto
    {
        [JsonPropertyName("thumbnail_large")]
        public PlaneSpottersThumbnail? ThumbnailLarge { get; set; }
    }

    private sealed class PlaneSpottersThumbnail
    {
        [JsonPropertyName("src")]
        public string? Src { get; set; }
    }
}
