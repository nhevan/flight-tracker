using System.Text;
using System.Text.Json;
using FlightTracker.Configuration;

namespace FlightTracker.Services;

public sealed class MapboxSnapshotService : IMapSnapshotService
{
    private readonly MapboxSettings _settings;
    private readonly HttpClient _httpClient;

    private const int ImageWidth  = 600;
    private const int ImageHeight = 400;

    public MapboxSnapshotService(AppSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings   = settings.Mapbox;
        _httpClient = httpClientFactory.CreateClient("Mapbox");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetSnapshotAsync(
        double? lat,
        double? lon,
        double? headingDegrees,
        double? altitudeMeters,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.AccessToken))
            return null;

        if (lat is null || lon is null)
            return null;

        try
        {
            int    zoom      = AltitudeToZoom(altitudeMeters);
            double distKm    = ZoomToTrajectoryKm(zoom);
            string overlays  = BuildOverlays(lat.Value, lon.Value, headingDegrees, distKm);
            string style     = string.IsNullOrWhiteSpace(_settings.Style) ? "mapbox/dark-v11" : _settings.Style;

            string url = $"https://api.mapbox.com/styles/v1/{style}/static" +
                         $"/{overlays}" +
                         $"/{lon.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{lat.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{zoom},0" +
                         $"/{ImageWidth}x{ImageHeight}@2x" +
                         $"?access_token={Uri.EscapeDataString(_settings.AccessToken)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MapSnapshot] Failed: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Calculates zoom level from barometric altitude in metres.</summary>
    private static int AltitudeToZoom(double? altMeters) => altMeters switch
    {
        > 9_144 => 9,   // > 30,000 ft — wide regional view
        > 3_048 => 11,  // 10,000–30,000 ft — city-level
        _       => 13   // < 10,000 ft — neighbourhood close-up
    };

    /// <summary>Returns how far (km) to project the trajectory line for a given zoom level.</summary>
    private static double ZoomToTrajectoryKm(int zoom) => zoom switch
    {
        9  => 80.0,
        11 => 30.0,
        _  => 10.0
    };

    /// <summary>
    /// Builds the Mapbox overlay string: a plane marker plus (optionally) a trajectory line.
    /// </summary>
    private static string BuildOverlays(double lat, double lon, double? headingDegrees, double distKm)
    {
        // Format lon/lat with 6 decimal places for the marker and GeoJSON
        string sLon = lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        string sLat = lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        string marker = $"pin-s-airport+ff0000({sLon},{sLat})";

        if (headingDegrees is null)
            return marker; // No heading — marker only

        // Project endpoint along heading (equirectangular, accurate to ±0.1% for < 100 km)
        double headingRad = headingDegrees.Value * Math.PI / 180.0;
        double lat2       = lat + (distKm / 111.0) * Math.Cos(headingRad);
        double lon2       = lon + (distKm / 111.0) * Math.Sin(headingRad) / Math.Cos(lat * Math.PI / 180.0);

        string sLon2 = lon2.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        string sLat2 = lat2.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        // Compact GeoJSON LineString with stroke styling
        var geoJson = new
        {
            type = "Feature",
            properties = new
            {
                stroke         = "#ffaa00",
                @stroke_width  = 3,
                stroke_opacity = 0.9
            },
            geometry = new
            {
                type        = "LineString",
                coordinates = new[] { new[] { lon, lat }, new[] { lon2, lat2 } }
            }
        };

        string geoJsonStr = JsonSerializer.Serialize(geoJson,
            new JsonSerializerOptions { PropertyNamingPolicy = null });

        // Replace stroke_width key — JsonSerializer adds underscore from C# convention
        geoJsonStr = geoJsonStr.Replace("stroke_width", "stroke-width");

        string encodedGeoJson = Uri.EscapeDataString(geoJsonStr);
        string line = $"geojson({encodedGeoJson})";

        return $"{marker},{line}";
    }
}
