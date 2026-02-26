namespace FlightTracker.Services;

using System.Net.Http.Headers;
using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

public sealed class OpenSkyService : IFlightService
{
    // Separate clients: _apiClient talks to opensky-network.org/api,
    // _authClient is managed by the shared OpenSkyTokenProvider.
    private readonly HttpClient _apiClient;
    private readonly AppSettings _settings;
    private readonly IOpenSkyTokenProvider _tokenProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenSkyService(
        IHttpClientFactory httpClientFactory,
        AppSettings settings,
        IOpenSkyTokenProvider tokenProvider)
    {
        _settings = settings;
        _tokenProvider = tokenProvider;

        _apiClient = httpClientFactory.CreateClient("opensky-api");
        _apiClient.BaseAddress = new Uri(settings.OpenSky.BaseUrl);
        _apiClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<FlightState>> GetOverheadFlightsAsync(
        CancellationToken cancellationToken)
    {
        var loc = _settings.HomeLocation;
        double radius = loc.BoundingBoxDegrees;

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
            _tokenProvider.Invalidate();
            (flights, _) = await TryFetchFlightsAsync(url, cancellationToken);
        }

        return flights;
    }

    private async Task<(IReadOnlyList<FlightState> Flights, bool IsUnauthorized)>
        TryFetchFlightsAsync(string url, CancellationToken cancellationToken)
    {
        string token = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response =
            await _apiClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return (Array.Empty<FlightState>(), true);

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
            .Where(f => rangeKm <= 0 || f.DistanceKm is null || f.DistanceKm <= rangeKm)
            .ToList()
            .AsReadOnly();

        return (flights, false);
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
