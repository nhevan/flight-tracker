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
        CancellationToken cancellationToken,
        IReadOnlyList<(double Lat, double Lon)>? trajectory = null)
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

            int    zoom     = _settings.ZoomOverride ?? DistanceToZoom(distToHomeKm);
            double halfKm   = ZoomToHalfTrajectoryKm(zoom);
            string overlays = BuildOverlays(lat.Value, lon.Value, _home.Latitude, _home.Longitude,
                                            effectiveHeading, halfKm, trajectory);
            string style    = string.IsNullOrWhiteSpace(_settings.Style) ? "mapbox/dark-v11" : _settings.Style;

            // Map is centred on HOME so the user sees the flight path relative to their location
            string url = $"https://api.mapbox.com/styles/v1/{style}/static" +
                         $"/{overlays}" +
                         $"/{_home.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{_home.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                         $"{zoom},350" +
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
        > 13 => 10,  // > 13 km  — wide regional view
        > 3  => 12,  // 3–13 km  — city-level
        _    => 14   // < 3 km   — neighbourhood
    };

    /// <summary>
    /// Half-length of the trajectory line (km) in each direction from the plane.
    /// Long enough that the full path through the home area is always visible.
    /// Each zoom step doubles pixel density (halves km coverage), so halfKm halves too:
    ///   zoom  8 → 160 km  |  zoom 10 → 40 km  |  zoom 12 → 10 km  |  zoom 14+ → 2.5 km
    /// Formula: 40 / 2^(zoom-10), clamped to a minimum of 2.5 km.
    /// </summary>
    private static double ZoomToHalfTrajectoryKm(int zoom) =>
        Math.Max(2.5, 40.0 / Math.Pow(2.0, zoom - 10));

    /// <summary>
    /// Builds the "backward" MultiLineString segment.
    /// When a full trajectory is available (≥ 2 points), returns a polyline through
    /// all historical positions ending with a 500 m gap before the plane pin.
    /// Falls back to the synthetic backward projection otherwise.
    /// </summary>
    private static JsonArray BuildBackSegment(
        IReadOnlyList<(double Lat, double Lon)>? trajectory,
        double planeLat, double planeLon,
        double cosLat,   double gapKm,
        double lonBwd,   double latBwd,
        double lonGapBwd, double latGapBwd)
    {
        if (trajectory is { Count: >= 2 })
        {
            // Inbound direction: bearing from second-to-last → last known position
            var prev = trajectory[^2];
            var last = trajectory[^1];
            double inboundRad = Math.Atan2(
                (last.Lon - prev.Lon) * cosLat * 111.0,
                (last.Lat - prev.Lat) * 111.0);

            // Gap point 500 m before the plane along the inbound direction
            double latGapInbound = planeLat - (gapKm / 111.0) * Math.Cos(inboundRad);
            double lonGapInbound = planeLon - (gapKm / 111.0) * Math.Sin(inboundRad) / cosLat;

            // Full path: all history points followed by the gap near the plane
            var pathCoords = new JsonArray();
            foreach (var (hLat, hLon) in trajectory)
                pathCoords.Add(new JsonArray { hLon, hLat });
            pathCoords.Add(new JsonArray { lonGapInbound, latGapInbound });
            return pathCoords;
        }

        // Fallback: synthetic two-point backward projection along current heading
        return new JsonArray { new JsonArray { lonBwd, latBwd }, new JsonArray { lonGapBwd, latGapBwd } };
    }

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
        double halfKm,
        IReadOnlyList<(double Lat, double Lon)>? trajectory = null)
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

        // Gap edge points — 500 m either side of the plane so the line doesn't overlap the pin
        double gapKm     = 0.5;
        double latGapFwd = planeLat + (gapKm / 111.0) * Math.Cos(headingRad);
        double lonGapFwd = planeLon + (gapKm / 111.0) * Math.Sin(headingRad) / cosLat;
        double latGapBwd = planeLat - (gapKm / 111.0) * Math.Cos(headingRad);
        double lonGapBwd = planeLon - (gapKm / 111.0) * Math.Sin(headingRad) / cosLat;

        // Closest point on the trajectory line to home — used to centre the arrowhead on
        // the visible portion of the line rather than on the (potentially off-map) plane.
        double dxToHome       = (homeLon - planeLon) * cosLat * 111.0;
        double dyToHome       = (homeLat - planeLat) * 111.0;
        double arrowProjKm    = dxToHome * Math.Sin(headingRad) + dyToHome * Math.Cos(headingRad);
        double arrowCenterLat = planeLat + (arrowProjKm / 111.0) * Math.Cos(headingRad);
        double arrowCenterLon = planeLon + (arrowProjKm / 111.0) * Math.Sin(headingRad) / cosLat;

        // Arrowhead triangle centred on the closest-approach point to home — size scales
        // with halfKm so it looks consistent across all zoom levels.
        double arrowDepthKm = halfKm * 0.03;   // full depth of the arrowhead
        double arrowHalfW   = halfKm * 0.025;  // half-width of the arrow base
        double perpRad      = headingRad + Math.PI / 2.0;
        double halfDepthKm  = arrowDepthKm / 2.0;

        double latTip   = arrowCenterLat + (halfDepthKm / 111.0) * Math.Cos(headingRad);
        double lonTip   = arrowCenterLon + (halfDepthKm / 111.0) * Math.Sin(headingRad) / cosLat;

        double latBase  = arrowCenterLat - (halfDepthKm / 111.0) * Math.Cos(headingRad);
        double lonBase  = arrowCenterLon - (halfDepthKm / 111.0) * Math.Sin(headingRad) / cosLat;

        double latRight = latBase + (arrowHalfW / 111.0) * Math.Cos(perpRad);
        double lonRight = lonBase + (arrowHalfW / 111.0) * Math.Sin(perpRad) / cosLat;

        double latLeft  = latBase - (arrowHalfW / 111.0) * Math.Cos(perpRad);
        double lonLeft  = lonBase - (arrowHalfW / 111.0) * Math.Sin(perpRad) / cosLat;

        // FeatureCollection: trajectory LineString + arrowhead Polygon centred on plane.
        // Built with JsonObject/JsonArray so property names with hyphens (stroke-width etc.)
        // are written directly — no fragile string-replace post-processing needed.
        static JsonArray Coord(double lon, double lat) => new() { lon, lat };

        var geoJson = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = new JsonArray
            {
                // Orange trajectory: two segments with a 1 km gap around the plane pin
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
                        ["type"] = "MultiLineString",
                        ["coordinates"] = new JsonArray
                        {
                            // behind: real accumulated trajectory if available,
                            // otherwise synthetic backward projection along current heading
                            BuildBackSegment(trajectory, planeLat, planeLon, cosLat, gapKm,
                                             lonBwd, latBwd, lonGapBwd, latGapBwd),
                            // ahead: 500 m after the plane → forward tip
                            new JsonArray { Coord(lonGapFwd, latGapFwd), Coord(lonFwd, latFwd) }
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
                                Coord(lonTip,   latTip),    // tip (half-depth ahead of plane)
                                Coord(lonRight, latRight),  // right base corner
                                Coord(lonLeft,  latLeft),   // left base corner
                                Coord(lonTip,   latTip)     // close ring
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
