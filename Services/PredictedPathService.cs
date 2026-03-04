namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FlightTracker.Helpers;
using FlightTracker.Models;

/// <summary>
/// Builds a predicted flight path from the filed route string.
///
/// Pipeline:
///   1. Fetch the route string from <see cref="IFlightAwareRouteService"/>.
///   2. Parse route tokens (SID, airway names, DCT, explicit fixes, STAR).
///   3. Resolve each fix token to lat/lon via <see cref="IArinc424NavDataService"/>.
///   4. Assemble the path: straight TF legs between consecutive fixes, with a fly-by
///      turn arc (computed by <see cref="FlyByArcHelper"/>) inserted at each waypoint.
///   5. Trim the path to start at the aircraft's current position.
///   6. Cache the result per callsign for the session lifetime.
///
/// Limitations (MVP):
///   • SID/STAR segment geometry is not available in the current ARINC file — the SID
///     departure fix and STAR arrival fix are treated as single waypoints.
///   • Airway intermediate fixes are not in the current ARINC file — only the entry/exit
///     fixes bounding each airway segment are plotted; the in-between route is a straight
///     great-circle line.
/// </summary>
public sealed class PredictedPathService : IPredictedPathService
{
    private readonly IFlightAwareRouteService  _faService;
    private readonly IArinc424NavDataService   _navData;

    // Regex: airway identifier = letters followed by digits (e.g. UL851, B9, M864, A1)
    private static readonly Regex AirwayRe = new(@"^[A-Z]+\d+$", RegexOptions.Compiled);

    // Regex: SID/STAR names typically end with a digit followed by a letter,
    // e.g. BERGI1A, LOGA2R, SUGOL2A — distinguishes them from plain fix names.
    private static readonly Regex ProcedureRe = new(@"^[A-Z]{2,5}\d[A-Z]$", RegexOptions.Compiled);

    // Session-lifetime cache: callsign → computed path (null = unavailable)
    private readonly ConcurrentDictionary<string, PredictedFlightPath?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PredictedPathService(
        IFlightAwareRouteService faService,
        IArinc424NavDataService  navData)
    {
        _faService = faService;
        _navData   = navData;
    }

    public async Task<PredictedFlightPath?> GetPredictedPathAsync(
        EnrichedFlightState flight,
        CancellationToken cancellationToken)
    {
        try
        {
            string callsign = flight.State.Callsign;
            if (string.IsNullOrWhiteSpace(callsign) || callsign == "N/A")
                return null;

            if (_cache.TryGetValue(callsign, out var cached))
                return cached;

            FiledRoute? filed = await _faService.GetFiledRouteAsync(callsign, cancellationToken);
            if (filed is null)
            {
                Console.WriteLine($"[PredictedPath] {callsign}: no filed route from FlightAware — path unavailable");
                var direct = BuildDirectPath(flight);
                _cache[callsign] = direct;
                return direct;
            }

            var path = BuildPath(filed, flight);
            _cache[callsign] = path;
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PredictedPath] {flight.State.Callsign}: {ex.Message}");
            return null;
        }
    }

    // ── Path building ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a straight great-circle path (origin → dest) when no filed route is available.
    /// Returns null if origin or destination coordinates are missing from the route.
    /// </summary>
    private PredictedFlightPath? BuildDirectPath(EnrichedFlightState ef)
    {
        string callsign = ef.State.Callsign;

        if (ef.Route?.OriginLat is null || ef.Route.OriginLon is null ||
            ef.Route.DestLat   is null || ef.Route.DestLon   is null)
        {
            Console.WriteLine($"[PredictedPath] {callsign}: no origin/dest coords — direct path unavailable");
            return null;
        }

        var points = new List<(double Lat, double Lon)>
        {
            (ef.Route.OriginLat.Value, ef.Route.OriginLon.Value),
            (ef.Route.DestLat.Value,   ef.Route.DestLon.Value),
        };

        // Trim to the portion ahead of the aircraft
        if (ef.State.Latitude.HasValue && ef.State.Longitude.HasValue)
            points = TrimToAhead(points, ef.State.Latitude.Value, ef.State.Longitude.Value);

        if (points.Count < 2)
        {
            Console.WriteLine($"[PredictedPath] {callsign}: direct path trimmed to {points.Count} point(s) — too short");
            return null;
        }

        Console.WriteLine($"[PredictedPath] {callsign}: no filed route — using direct path (origin → dest)");
        return new PredictedFlightPath(points.AsReadOnly(), IsDirect: true);
    }

    private PredictedFlightPath? BuildPath(FiledRoute filed, EnrichedFlightState ef)
    {
        double hintLat = ef.State.Latitude  ?? 52.0;
        double hintLon = ef.State.Longitude ?? 4.5;

        // 1. Parse route string into an ordered list of fix coordinates
        var fixes = ParseRouteToFixes(filed.RouteString, hintLat, hintLon, ef);
        if (fixes.Count < 2)
        {
            Console.WriteLine($"[PredictedPath] {filed.Callsign}: only {fixes.Count} fix(es) resolved — skipping.");
            return null;
        }

        // 2. Build the smoothed path with fly-by arcs
        double tasMs = ef.State.VelocityMetersPerSecond ?? 200.0; // default ~390 kt if unknown
        var points = new List<(double Lat, double Lon)>(fixes.Count * 4);

        points.Add(fixes[0]); // start with the first fix

        for (int i = 1; i < fixes.Count; i++)
        {
            bool hasPrev = i >= 1;
            bool hasNext = i < fixes.Count - 1;

            if (hasPrev && hasNext)
            {
                var (pLat, pLon) = fixes[i - 1];
                var (wLat, wLon) = fixes[i];
                var (nLat, nLon) = fixes[i + 1];

                var arc = FlyByArcHelper.BuildFlyByArc(pLat, pLon, wLat, wLon, nLat, nLon, tasMs);
                if (arc.Count > 0)
                {
                    // Straight segment to arc start, then the arc, continue from arc end
                    points.Add(arc[0]);
                    points.AddRange(arc);
                    // We do NOT add the waypoint itself — the arc bypasses it
                    continue;
                }
            }

            // No arc (last fix, or arc skipped): add waypoint directly
            points.Add(fixes[i]);
        }

        // 3. Trim to the portion ahead of the aircraft's current position
        if (ef.State.Latitude.HasValue && ef.State.Longitude.HasValue)
            points = TrimToAhead(points, ef.State.Latitude.Value, ef.State.Longitude.Value);

        if (points.Count < 2)
        {
            Console.WriteLine($"[PredictedPath] {filed.Callsign}: path trimmed to {points.Count} point(s) — too short");
            return null;
        }

        Console.WriteLine($"[PredictedPath] {filed.Callsign}: {points.Count} path points from {fixes.Count} fixes.");
        return new PredictedFlightPath(points.AsReadOnly());
    }

    // ── Route string parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Converts a route string into an ordered list of resolved (lat, lon) coordinates.
    ///
    /// Route string format examples:
    ///   BERGI1A ARNEM UL851 BEGAR DCT LOGAN LOGA2R
    ///   SPL DCT SUGOL DCT ARTIP DCT EDUPO
    ///
    /// Token classification (left to right):
    ///   • "DCT" keyword → skip
    ///   • Airway token (letters+digits, e.g. UL851) → skip (entry/exit fixes are the surrounding tokens)
    ///   • Anything else → attempt to resolve as a fix
    ///
    /// SID and STAR token names (e.g. BERGI1A, LOGA2R) are resolved as single waypoints:
    /// we extract the procedure name prefix (the fix name portion) and try that.
    /// </summary>
    private List<(double Lat, double Lon)> ParseRouteToFixes(
        string routeString,
        double hintLat, double hintLon,
        EnrichedFlightState ef)
    {
        // Also prepend origin and append destination coordinates if available
        var result = new List<(double, double)>();

        if (ef.Route?.OriginLat.HasValue == true && ef.Route.OriginLon.HasValue == true)
            result.Add((ef.Route.OriginLat!.Value, ef.Route.OriginLon!.Value));

        string[] tokens = routeString.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            // Skip DCT and airway identifiers
            if (token.Equals("DCT", StringComparison.OrdinalIgnoreCase))
                continue;
            if (AirwayRe.IsMatch(token))
                continue;

            // Try to resolve the token as a fix.
            // For SID/STAR names like "BERGI1A", try the 5-char prefix first ("BERGI"),
            // then the full token.
            NavFix? fix = null;
            if (ProcedureRe.IsMatch(token) && token.Length > 2)
            {
                // Extract the alphabetic prefix (the transition fix name)
                string prefix = new string(token.TakeWhile(char.IsLetter).ToArray());
                if (!string.IsNullOrEmpty(prefix))
                    fix = _navData.TryResolveFix(prefix, hintLat, hintLon);
            }

            fix ??= _navData.TryResolveFix(token, hintLat, hintLon);

            if (fix is not null)
            {
                // Avoid duplicate consecutive coordinates
                if (result.Count == 0 ||
                    Math.Abs(result[^1].Item1 - fix.Lat) > 0.0001 ||
                    Math.Abs(result[^1].Item2 - fix.Lon) > 0.0001)
                {
                    result.Add((fix.Lat, fix.Lon));
                }
            }
        }

        if (ef.Route?.DestLat.HasValue == true && ef.Route.DestLon.HasValue == true)
        {
            var dest = (ef.Route.DestLat!.Value, ef.Route.DestLon!.Value);
            if (result.Count == 0 || result[^1] != dest)
                result.Add(dest);
        }

        return result;
    }

    // ── Path trimming ─────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the path segment closest to the aircraft's current position and returns
    /// everything from that point forward (i.e., the "ahead" portion of the path).
    /// This prevents drawing the path over already-flown waypoints.
    /// </summary>
    private static List<(double Lat, double Lon)> TrimToAhead(
        List<(double Lat, double Lon)> path,
        double acLat, double acLon)
    {
        if (path.Count < 2)
            return path;

        int closestIdx = 0;
        double minDistSq = double.MaxValue;

        for (int i = 0; i < path.Count; i++)
        {
            double dLat = path[i].Lat - acLat;
            double dLon = path[i].Lon - acLon;
            double dSq  = dLat * dLat + dLon * dLon;
            if (dSq < minDistSq)
            {
                minDistSq = dSq;
                closestIdx = i;
            }
        }

        // Prepend the aircraft's current position so the blue line starts right at the plane
        var ahead = new List<(double, double)>(path.Count - closestIdx + 1);
        ahead.Add((acLat, acLon));
        ahead.AddRange(path.Skip(closestIdx));
        return ahead;
    }
}
