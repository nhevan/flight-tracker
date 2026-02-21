namespace FlightTracker.Services;

using System.Net.Http.Headers;
using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

public sealed class OpenSkyService : IFlightService
{
    // Separate clients: _apiClient talks to opensky-network.org/api,
    // _authClient talks to auth.opensky-network.org (different host — cannot share BaseAddress)
    private readonly HttpClient _apiClient;
    private readonly HttpClient _authClient;
    private readonly AppSettings _settings;

    // ── Token cache ──────────────────────────────────────────────────────────
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    // Ensures only one concurrent token fetch even if polls overlap
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Reused across all deserialization calls
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenSkyService(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _settings = settings;

        _apiClient = httpClientFactory.CreateClient("opensky-api");
        _apiClient.BaseAddress = new Uri(settings.OpenSky.BaseUrl);
        _apiClient.Timeout = TimeSpan.FromSeconds(15);

        _authClient = httpClientFactory.CreateClient("opensky-auth");
        _authClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyList<FlightState>> GetOverheadFlightsAsync(
        CancellationToken cancellationToken)
    {
        var loc = _settings.HomeLocation;
        double radius = loc.BoundingBoxDegrees;

        // Clamp latitude to valid WGS-84 range
        double lamin = Math.Max(-90.0, loc.Latitude - radius);
        double lamax = Math.Min(90.0, loc.Latitude + radius);
        double lomin = loc.Longitude - radius;
        double lomax = loc.Longitude + radius;

        // Use InvariantCulture to ensure decimal points, not locale-specific commas
        string url = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"states/all?lamin={lamin}&lomin={lomin}&lamax={lamax}&lomax={lomax}");

        // First attempt
        var (flights, isUnauthorized) = await TryFetchFlightsAsync(url, cancellationToken);

        if (isUnauthorized)
        {
            // Token may have expired mid-interval — invalidate cache and retry once
            _cachedToken = null;
            (flights, _) = await TryFetchFlightsAsync(url, cancellationToken);
        }

        return flights;
    }

    // Returns the flight list and a flag indicating whether a 401 was received.
    private async Task<(IReadOnlyList<FlightState> Flights, bool IsUnauthorized)>
        TryFetchFlightsAsync(string url, CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response =
            await _apiClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return (Array.Empty<FlightState>(), true);

        // 429: rate-limited — return empty list silently; loop retries after interval
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return (Array.Empty<FlightState>(), false);

        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"API returned {(int)response.StatusCode}: {errBody}",
                null, response.StatusCode);
        }

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        var raw = await JsonSerializer.DeserializeAsync<OpenSkyResponse>(
            stream, JsonOptions, cancellationToken);

        if (raw?.States is null)
            return (Array.Empty<FlightState>(), false);

        var loc = _settings.HomeLocation;
        double rangeKm = loc.VisualRangeKm;

        var flights = raw.States
            .Select(s => MapToFlightState(s, loc.Latitude, loc.Longitude))
            .OfType<FlightState>()
            .Where(f => !f.OnGround)
            // Filter by visual range when VisualRangeKm > 0 and position is known
            .Where(f => rangeKm <= 0 || f.DistanceKm is null || f.DistanceKm <= rangeKm)
            .ToList()
            .AsReadOnly();

        return (flights, false);
    }

    // ── OAuth2 Client Credentials token fetch with caching ───────────────────

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Return cached token if it won't expire in the next 60 seconds
            if (_cachedToken is not null &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromSeconds(60))
            {
                return _cachedToken;
            }

            // Fetch a new token — uses dedicated auth client (no BaseAddress conflict)
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _settings.OpenSky.ClientId,
                ["client_secret"] = _settings.OpenSky.ClientSecret,
            });

            using HttpResponseMessage tokenResponse =
                await _authClient.PostAsync(_settings.OpenSky.TokenUrl, body, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                string errBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Token endpoint returned {(int)tokenResponse.StatusCode}: {errBody}",
                    null, tokenResponse.StatusCode);
            }

            await using Stream tokenStream =
                await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);

            var tokenData = await JsonSerializer.DeserializeAsync<TokenResponse>(
                tokenStream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Token endpoint returned an empty or invalid response.");

            _cachedToken = tokenData.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenData.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ── OpenSky state vector mapping ─────────────────────────────────────────

    // OpenSky state vector array indices:
    // [0]  icao24          string
    // [1]  callsign        string | null
    // [2]  origin_country  string
    // [5]  longitude       float | null
    // [6]  latitude        float | null
    // [7]  baro_altitude   float | null
    // [8]  on_ground       bool
    // [9]  velocity        float | null
    // [10] true_track      float | null  (heading, degrees clockwise from North)
    // [11] vertical_rate   float | null
    private static FlightState? MapToFlightState(JsonElement[] state, double homeLat, double homeLon)
    {
        if (state.Length < 11) return null;

        double? lon = GetDouble(state, 5);
        double? lat = GetDouble(state, 6);

        double? distanceKm = (lat.HasValue && lon.HasValue)
            ? Haversine.DistanceKm(homeLat, homeLon, lat.Value, lon.Value)
            : null;

        return new FlightState
        {
            Icao24                      = GetString(state, 0) ?? string.Empty,
            Callsign                    = (GetString(state, 1) ?? "N/A").Trim(),
            OriginCountry               = GetString(state, 2) ?? "Unknown",
            Longitude                   = lon,
            Latitude                    = lat,
            BarometricAltitudeMeters    = GetDouble(state, 7),
            OnGround                    = GetBool(state, 8),
            VelocityMetersPerSecond     = GetDouble(state, 9),
            HeadingDegrees              = GetDouble(state, 10),
            VerticalRateMetersPerSecond = GetDouble(state, 11),
            DistanceKm                  = distanceKm,
        };
    }

    private static string? GetString(JsonElement[] arr, int i) =>
        arr[i].ValueKind == JsonValueKind.Null ? null : arr[i].GetString();

    private static double? GetDouble(JsonElement[] arr, int i) =>
        arr[i].ValueKind == JsonValueKind.Null ? null : arr[i].GetDouble();

    private static bool GetBool(JsonElement[] arr, int i) =>
        arr[i].ValueKind != JsonValueKind.Null && arr[i].GetBoolean();
}
