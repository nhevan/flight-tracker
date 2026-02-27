namespace FlightTracker.Services;

using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

public sealed class AirplanesLiveService : IFlightService
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AirplanesLiveService(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _settings = settings;

        _httpClient = httpClientFactory.CreateClient("airplanes-live");
        _httpClient.BaseAddress = new Uri(settings.AirplanesLive.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
    }

    public async Task<IReadOnlyList<FlightState>> GetOverheadFlightsAsync(
        CancellationToken cancellationToken)
    {
        var loc = _settings.HomeLocation;

        // 1 degree ≈ 60 NM — convert bounding-box half-width to a search radius
        int radiusNm = (int)Math.Ceiling(loc.BoundingBoxDegrees * 60);

        string url = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"point/{loc.Latitude}/{loc.Longitude}/{radiusNm}");

        using HttpResponseMessage response =
            await _httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return Array.Empty<FlightState>();

        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"airplanes.live returned {(int)response.StatusCode}: {errBody}",
                null, response.StatusCode);
        }

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        var raw = await JsonSerializer.DeserializeAsync<AirplanesLiveResponse>(
            stream, JsonOptions, cancellationToken);

        if (raw?.Ac is null)
            return Array.Empty<FlightState>();

        double rangeKm = loc.VisualRangeKm;

        var flights = raw.Ac
            .Select(a => MapToFlightState(a, loc.Latitude, loc.Longitude))
            .OfType<FlightState>()
            .Where(f => !f.OnGround)
            .Where(f => rangeKm <= 0 || f.DistanceKm is null || f.DistanceKm <= rangeKm)
            .ToList()
            .AsReadOnly();

        return flights;
    }

    private static FlightState? MapToFlightState(
        AirplanesLiveAircraft a, double homeLat, double homeLon)
    {
        if (string.IsNullOrEmpty(a.Hex)) return null;

        // alt_baro is either an integer (feet) or the string "ground"
        bool onGround = a.AltBaro.ValueKind == JsonValueKind.String
                        && a.AltBaro.GetString() == "ground";

        double? altMeters = (!onGround && a.AltBaro.ValueKind == JsonValueKind.Number)
            ? a.AltBaro.GetDouble() * 0.3048   // feet → metres
            : null;

        double? distanceKm = (a.Lat.HasValue && a.Lon.HasValue)
            ? Haversine.DistanceKm(homeLat, homeLon, a.Lat.Value, a.Lon.Value)
            : null;

        return new FlightState
        {
            Icao24                      = a.Hex,
            Callsign                    = (a.Flight ?? "N/A").Trim(),
            OriginCountry               = string.Empty,   // not provided by airplanes.live
            Longitude                   = a.Lon,
            Latitude                    = a.Lat,
            BarometricAltitudeMeters    = altMeters,
            OnGround                    = onGround,
            VelocityMetersPerSecond     = a.Gs.HasValue ? a.Gs.Value * 0.514444 : null,  // knots → m/s
            HeadingDegrees              = a.Track,
            VerticalRateMetersPerSecond = a.BaroRate.HasValue ? a.BaroRate.Value * 0.00508 : null, // ft/min → m/s
            DistanceKm                  = distanceKm,
        };
    }
}
