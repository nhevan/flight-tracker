namespace FlightTracker.Services;

using System.Collections.Concurrent;
using FlightTracker.Helpers;
using FlightTracker.Models;

/// <summary>
/// Builds a predicted flight path using the Navigraph SQLite nav-data.
///
/// Pipeline:
///   1. Try airway snapping: find the nearest heading-aligned airway segment
///      near the aircraft and follow its ordered waypoints toward the destination
///      (via <see cref="INavigraphNavDataService.GetAirwayPath"/>).
///   2. Fall back to a direct great-circle line (origin → dest) when no airway
///      is found or the aircraft is within ~150 km of the destination.
///   3. Trim the result so the blue line starts at the aircraft's current position.
///   4. Cache the result per callsign for the session lifetime.
/// </summary>
public sealed class PredictedPathService : IPredictedPathService
{
    private readonly INavigraphNavDataService _navData;

    // Session-lifetime cache: callsign → computed path (null = unavailable)
    private readonly ConcurrentDictionary<string, PredictedFlightPath?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PredictedPathService(INavigraphNavDataService navData)
    {
        _navData = navData;
    }

    public void InvalidateCache(string callsign) =>
        _cache.TryRemove(callsign, out _);

    public Task<PredictedFlightPath?> GetPredictedPathAsync(
        EnrichedFlightState flight,
        CancellationToken cancellationToken)
    {
        try
        {
            string callsign = flight.State.Callsign;
            if (string.IsNullOrWhiteSpace(callsign) || callsign == "N/A")
                return Task.FromResult<PredictedFlightPath?>(null);

            if (_cache.TryGetValue(callsign, out var cached))
                return Task.FromResult(cached);

            var path = BuildPath(flight);
            _cache[callsign] = path;
            return Task.FromResult(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PredictedPath] {flight.State.Callsign}: {ex.Message}");
            return Task.FromResult<PredictedFlightPath?>(null);
        }
    }

    // ── Path building ─────────────────────────────────────────────────────────

    private PredictedFlightPath? BuildPath(EnrichedFlightState ef)
    {
        string callsign = ef.State.Callsign;

        double? acLat   = ef.State.Latitude;
        double? acLon   = ef.State.Longitude;
        double? destLat = ef.Route?.DestLat;
        double? destLon = ef.Route?.DestLon;

        if (acLat is null || acLon is null || destLat is null || destLon is null)
        {
            Console.WriteLine($"[PredictedPath] {callsign}: missing position or destination — path unavailable");
            return null;
        }

        // 1. Try airway snapping (always — bearing is computed from position→destination internally)
        double distToDestKm = Haversine.DistanceKm(acLat.Value, acLon.Value, destLat.Value, destLon.Value);

        if (_navData.IsAvailable)
        {
            // Skip airway search when near destination (same threshold as NavigraphNavDataService)
            if (distToDestKm < 150.0)
            {
                string nearDestLog = $"Navigraph: on approach ({distToDestKm:F0} km to dest) — direct path";
                Console.WriteLine($"[PredictedPath] {callsign}: within 150km of dest ({distToDestKm:F0}km) — direct path");
                return BuildDirectPath(ef, nearDestLog);
            }

            var result = _navData.GetAirwayPath(
                acLat.Value, acLon.Value,
                destLat.Value, destLon.Value);

            if (result is not null && result.Points.Count >= 2)
            {
                string chainStr = result.AirwaysUsed.Count > 1
                    ? string.Join(" → ", result.AirwaysUsed)
                    : result.AirwayName;
                string log = $"Navigraph ✓ {chainStr} · {result.Points.Count} wpts ({result.SegmentsScanned} segs scanned)\nARINC: terminal area only (4 NL fixes, not applicable)";
                Console.WriteLine($"[PredictedPath] {callsign}: airway path with {result.Points.Count} points via {chainStr}");
                return new PredictedFlightPath(result.Points.AsReadOnly(), NavDataLog: log);
            }

            string segsInfo = result?.SegmentsScanned.ToString() ?? "0";
            string noAirwayLog = $"Navigraph ✗ no airway matched ({segsInfo} segs scanned)\nARINC: terminal area only (4 NL fixes, not applicable)\nFallback: direct path";
            Console.WriteLine($"[PredictedPath] {callsign}: no airway found — falling back to direct path");
            return BuildDirectPath(ef, noAirwayLog);
        }

        // DB not available
        string dbMissingLog = "Navigraph ✗ DB not found — copy little_navmap_navigraph.sqlite to flightLegDataArinc/\nFallback: direct path";
        Console.WriteLine($"[PredictedPath] {callsign}: Navigraph DB not available — falling back to direct path");
        return BuildDirectPath(ef, dbMissingLog);
    }

    private PredictedFlightPath? BuildDirectPath(EnrichedFlightState ef, string? navDataLog = null)
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

        if (ef.State.Latitude.HasValue && ef.State.Longitude.HasValue)
            points = TrimToAhead(points, ef.State.Latitude.Value, ef.State.Longitude.Value);

        if (points.Count < 2)
        {
            Console.WriteLine($"[PredictedPath] {callsign}: direct path trimmed to {points.Count} point(s) — too short");
            return null;
        }

        string log = navDataLog ?? "Navigraph: skipped\nARINC: terminal area only (4 NL fixes, not applicable)\nFallback: direct path";
        Console.WriteLine($"[PredictedPath] {callsign}: using direct path (origin → dest)");
        return new PredictedFlightPath(points.AsReadOnly(), IsDirect: true, NavDataLog: log);
    }

    // ── Path trimming ─────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the path point closest to the aircraft's current position and returns
    /// everything from that point forward, prepending the aircraft's current position.
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

        var ahead = new List<(double, double)>(path.Count - closestIdx + 1);
        ahead.Add((acLat, acLon));
        ahead.AddRange(path.Skip(closestIdx));
        return ahead;
    }
}
