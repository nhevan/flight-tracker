using System.Text.Json;
using FlightTracker.Configuration;

namespace FlightTracker.Services;

public sealed class MapboxSnapshotService : IMapSnapshotService
{
    private readonly MapboxSettings        _settings;
    private readonly HomeLocationSettings  _home;
    private readonly HttpClient            _httpClient;

    private const int ImageWidth  = 600;
    private const int ImageHeight = 400;

    public MapboxSnapshotService(AppSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings   = settings.Mapbox;
        _home       = settings.HomeLocation;
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
            // Distance from plane to home (equirectangular, < 0.1% error within 100 km)
            double dlat         = (_home.Latitude  - lat.Value) * 111.0;
            double dlon         = (_home.Longitude - lon.Value) * 111.0 * Math.Cos(lat.Value * Math.PI / 180.0);
            double distToHomeKm = Math.Sqrt(dlat * dlat + dlon * dlon);

            int    zoom     = DistanceToZoom(distToHomeKm);
            double halfKm   = ZoomToHalfTrajectoryKm(zoom);
            string overlays = BuildOverlays(lat.Value, lon.Value, _home.Latitude, _home.Longitude,
                                            headingDegrees, halfKm);
            string style    = string.IsNullOrWhiteSpace(_settings.Style) ? "mapbox/dark-v11" : _settings.Style;

            // Map is centred on HOME so the user sees the flight path relative to their location
            string url = $"https://api.mapbox.com/styles/v1/{style}/static" +
                         $"/{overlays}" +
                         $"/{_home.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{_home.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
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

    /// <summary>
    /// Zoom level based on plane-to-home distance so the plane marker is always
    /// visible on the home-centred map.
    /// </summary>
    private static int DistanceToZoom(double distKm) => distKm switch
    {
        > 30 => 9,   // > 30 km  — wide regional view  (~180 km across)
        > 10 => 11,  // 10–30 km — city-level           (~45 km across)
        _    => 13   // < 10 km  — neighbourhood        (~11 km across)
    };

    /// <summary>
    /// Half-length of the trajectory line (km) in each direction from the plane.
    /// Long enough that the full path through the home area is always visible.
    /// </summary>
    private static double ZoomToHalfTrajectoryKm(int zoom) => zoom switch
    {
        9  => 100.0,
        11 => 40.0,
        _  => 15.0
    };

    /// <summary>
    /// Builds the Mapbox overlay string:
    ///   • Red  airport pin — plane's current position
    ///   • Blue home    pin — user's home (map centre reference)
    ///   • Orange LineString — trajectory extending <halfKm> km both behind
    ///     and ahead of the plane so the full flight path through home is visible.
    /// </summary>
    private static string BuildOverlays(
        double planeLat, double planeLon,
        double homeLat,  double homeLon,
        double? headingDegrees,
        double halfKm)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;

        // Red airport pin — plane's current position
        string planeMarker = $"pin-s-airport+ff0000({planeLon.ToString("F6", ic)},{planeLat.ToString("F6", ic)})";

        // Blue home pin — shown at the map centre so the user can see their reference point
        string homeMarker = $"pin-s-home+4499ff({homeLon.ToString("F6", ic)},{homeLat.ToString("F6", ic)})";

        if (headingDegrees is null)
            return $"{planeMarker},{homeMarker}";

        // Project halfKm forward and backward along the plane's heading
        double headingRad = headingDegrees.Value * Math.PI / 180.0;
        double cosLat     = Math.Cos(planeLat * Math.PI / 180.0);

        double latFwd = planeLat + (halfKm / 111.0) * Math.Cos(headingRad);
        double lonFwd = planeLon + (halfKm / 111.0) * Math.Sin(headingRad) / cosLat;

        double latBwd = planeLat - (halfKm / 111.0) * Math.Cos(headingRad);
        double lonBwd = planeLon - (halfKm / 111.0) * Math.Sin(headingRad) / cosLat;

        // 3-point LineString: behind the plane → current position → ahead of the plane
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
                coordinates = new[]
                {
                    new[] { lonBwd,   latBwd   },   // behind
                    new[] { planeLon, planeLat },   // plane now
                    new[] { lonFwd,   latFwd   }    // ahead
                }
            }
        };

        string geoJsonStr = JsonSerializer.Serialize(geoJson,
            new JsonSerializerOptions { PropertyNamingPolicy = null });

        // JsonSerializer writes "stroke_width"; GeoJSON spec requires "stroke-width"
        geoJsonStr = geoJsonStr.Replace("stroke_width", "stroke-width");

        string encodedGeoJson = Uri.EscapeDataString(geoJsonStr);

        return $"{planeMarker},{homeMarker},geojson({encodedGeoJson})";
    }
}
