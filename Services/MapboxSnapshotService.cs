using System.Text.Json;
using System.Text.Json.Nodes;
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
        double? inferredHeadingDegrees,
        double? altitudeMeters,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.AccessToken))
            return null;

        if (lat is null || lon is null)
            return null;

        // Without any heading data the map shows no meaningful trajectory — skip it.
        // Fall back to the inferred heading (derived from GPS position delta) if available.
        double? effectiveHeading = headingDegrees ?? inferredHeadingDegrees;
        if (effectiveHeading is null)
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
                                            effectiveHeading, halfKm);
            string style    = string.IsNullOrWhiteSpace(_settings.Style) ? "mapbox/dark-v11" : _settings.Style;

            // Map is centred on HOME so the user sees the flight path relative to their location
            string url = $"https://api.mapbox.com/styles/v1/{style}/static" +
                         $"/{overlays}" +
                         $"/{_home.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{_home.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{zoom},0" +
                         $"/{ImageWidth}x{ImageHeight}@2x" +
                         $"?access_token={Uri.EscapeDataString(_settings.AccessToken)}";

            Console.WriteLine($"[MapSnapshot] URL: {url.Replace(_settings.AccessToken, "***")}");

            // Use DangerousDisablePathAndQueryCanonicalization so the .NET Uri class does not
            // decode percent-encoded characters (e.g. %2C → ,) in the path segment that
            // contains the URL-encoded GeoJSON.  Without this, the Uri normalisation step
            // inside HttpClient can silently corrupt the encoded GeoJSON before it reaches
            // the Mapbox API, causing the overlay to be silently ignored.
            Uri.TryCreate(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }, out Uri? safeUri);
            var response = await _httpClient.GetAsync(safeUri ?? new Uri(url), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[MapSnapshot] Mapbox error {(int)response.StatusCode}: {body}");
                return null;
            }
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
    ///
    /// At lat ~52 °N a 600 px-wide image covers approximately:
    ///   zoom 9  → ~113 km  (half-width ~56 km)
    ///   zoom 11 → ~28 km   (half-width ~14 km)
    ///   zoom 13 → ~7 km    (half-width ~3.5 km)
    /// Thresholds are set conservatively below each half-width so the plane
    /// marker always falls inside the image bounds.
    /// </summary>
    private static int DistanceToZoom(double distKm) => distKm switch
    {
        > 13 => 9,   // > 13 km  — wide regional view  (~113 km across)
        > 3  => 11,  // 3–13 km  — city-level           (~28 km across)
        _    => 13   // < 3 km   — neighbourhood         (~7 km across)
    };

    /// <summary>
    /// Half-length of the trajectory line (km) in each direction from the plane.
    /// Long enough that the full path through the home area is always visible.
    /// </summary>
    private static double ZoomToHalfTrajectoryKm(int zoom) => zoom switch
    {
        9  => 80.0,
        11 => 20.0,
        _  => 5.0
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

        // Defensive guard — GetSnapshotAsync already returns null when heading is null,
        // but kept here in case BuildOverlays is ever called directly.
        if (headingDegrees is null)
            return $"{planeMarker},{homeMarker}";

        // Project halfKm forward and backward along the plane's heading
        double headingRad = headingDegrees.Value * Math.PI / 180.0;
        double cosLat     = Math.Cos(planeLat * Math.PI / 180.0);

        double latFwd = planeLat + (halfKm / 111.0) * Math.Cos(headingRad);
        double lonFwd = planeLon + (halfKm / 111.0) * Math.Sin(headingRad) / cosLat;

        double latBwd = planeLat - (halfKm / 111.0) * Math.Cos(headingRad);
        double lonBwd = planeLon - (halfKm / 111.0) * Math.Sin(headingRad) / cosLat;

        // Arrowhead triangle at the forward tip — size scales with halfKm so it looks
        // consistent across all zoom levels
        double arrowDepthKm = halfKm * 0.25;   // how far back from tip to the base
        double arrowHalfW   = halfKm * 0.15;   // half-width of the arrow base
        double perpRad      = headingRad + Math.PI / 2.0;

        double latBase  = latFwd - (arrowDepthKm / 111.0) * Math.Cos(headingRad);
        double lonBase  = lonFwd - (arrowDepthKm / 111.0) * Math.Sin(headingRad) / cosLat;

        double latRight = latBase + (arrowHalfW / 111.0) * Math.Cos(perpRad);
        double lonRight = lonBase + (arrowHalfW / 111.0) * Math.Sin(perpRad) / cosLat;

        double latLeft  = latBase - (arrowHalfW / 111.0) * Math.Cos(perpRad);
        double lonLeft  = lonBase - (arrowHalfW / 111.0) * Math.Sin(perpRad) / cosLat;

        // FeatureCollection: trajectory LineString + arrowhead Polygon at the forward tip.
        // Built with JsonObject/JsonArray so property names with hyphens (stroke-width etc.)
        // are written directly — no fragile string-replace post-processing needed.
        static JsonArray Coord(double lon, double lat) => new() { lon, lat };

        var geoJson = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = new JsonArray
            {
                // Orange trajectory line: behind → plane → ahead
                new JsonObject
                {
                    ["type"] = "Feature",
                    ["properties"] = new JsonObject
                    {
                        ["stroke"]         = "#ffaa00",
                        ["stroke-width"]   = 3,
                        ["stroke-opacity"] = 0.9
                    },
                    ["geometry"] = new JsonObject
                    {
                        ["type"] = "LineString",
                        ["coordinates"] = new JsonArray
                        {
                            Coord(lonBwd,   latBwd),
                            Coord(planeLon, planeLat),
                            Coord(lonFwd,   latFwd)
                        }
                    }
                },
                // Filled arrowhead triangle pointing in the direction of travel
                new JsonObject
                {
                    ["type"] = "Feature",
                    ["properties"] = new JsonObject
                    {
                        ["fill"]           = "#ffaa00",
                        ["fill-opacity"]   = 0.9,
                        ["stroke"]         = "#ffaa00",
                        ["stroke-width"]   = 1,
                        ["stroke-opacity"] = 0.9
                    },
                    ["geometry"] = new JsonObject
                    {
                        ["type"] = "Polygon",
                        ["coordinates"] = new JsonArray
                        {
                            new JsonArray   // outer ring
                            {
                                Coord(lonFwd,   latFwd),    // tip
                                Coord(lonRight, latRight),  // right base corner
                                Coord(lonLeft,  latLeft),   // left base corner
                                Coord(lonFwd,   latFwd)     // close ring
                            }
                        }
                    }
                }
            }
        };

        string geoJsonStr = geoJson.ToJsonString();
        Console.WriteLine($"[MapSnapshot] GeoJSON: {geoJsonStr}");

        string encodedGeoJson = Uri.EscapeDataString(geoJsonStr);

        return $"{planeMarker},{homeMarker},geojson({encodedGeoJson})";
    }
}
