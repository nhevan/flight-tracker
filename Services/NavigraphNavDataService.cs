namespace FlightTracker.Services;

using Microsoft.Data.Sqlite;
using FlightTracker.Helpers;
using FlightTracker.Models;

/// <summary>
/// Fix resolution and airway-based path inference backed by the
/// Navigraph / Little NavMap SQLite database.
///
/// Fix lookup: queries the <c>waypoint</c> table (206 000+ worldwide fixes).
/// Airway snapping: queries the <c>airway</c> table (86 000+ segments).
/// </summary>
public sealed class NavigraphNavDataService : INavigraphNavDataService
{
    private readonly string _dbPath;

    // How many degrees either side of aircraft heading we consider "aligned"
    private const double HeadingToleranceDeg = 35.0;

    // Bounding-box half-width used for candidate segment search (degrees)
    private const double SearchHalfDegLat = 0.7;   // ≈ 78 km
    private const double SearchHalfDegLon = 1.0;   // ≈ 70 km at 52°N

    // Once the aircraft is within this distance of the destination, stop
    // following airways and draw direct.
    private const double StarCutoffKm = 150.0;

    public NavigraphNavDataService(string dbPath)
    {
        _dbPath = dbPath;
        if (!File.Exists(dbPath))
            Console.WriteLine($"[Navigraph] WARNING: database not found at {dbPath}");
        else
            Console.WriteLine($"[Navigraph] Database loaded: {dbPath}");
    }

    // ── Fix resolution ────────────────────────────────────────────────────────

    public NavFix? TryResolveFix(string name, double hintLat, double hintLon)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string upper = name.Trim().ToUpperInvariant();

        try
        {
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ident, laty, lonx
                FROM   waypoint
                WHERE  ident = @name
                ORDER BY ((laty - @lat) * (laty - @lat) + (lonx - @lon) * (lonx - @lon))
                LIMIT  1
                """;
            cmd.Parameters.AddWithValue("@name", upper);
            cmd.Parameters.AddWithValue("@lat",  hintLat);
            cmd.Parameters.AddWithValue("@lon",  hintLon);

            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return null;

            return new NavFix(
                rdr.GetString(0),
                rdr.GetDouble(1),
                rdr.GetDouble(2),
                NavFixType.Waypoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Navigraph] TryResolveFix({name}): {ex.Message}");
            return null;
        }
    }

    // ── Airway snapping ───────────────────────────────────────────────────────

    public List<(double Lat, double Lon)>? GetAirwayPath(
        double acLat, double acLon, double acHeadingDeg,
        double destLat, double destLon)
    {
        try
        {
            // If already close to destination, no point snapping to airways.
            if (Haversine.DistanceKm(acLat, acLon, destLat, destLon) < StarCutoffKm)
                return null;

            var candidate = FindBestSegment(acLat, acLon, acHeadingDeg, destLat, destLon);
            if (candidate is null) return null;

            var waypoints = FollowAirway(
                candidate.AirwayName,
                candidate.FragmentNo,
                candidate.SequenceNo,
                candidate.ForwardDirection,
                destLat, destLon);

            if (waypoints.Count < 2) return null;

            // Prepend aircraft position, append destination
            var path = new List<(double Lat, double Lon)>(waypoints.Count + 2);
            path.Add((acLat, acLon));
            path.AddRange(waypoints);
            if (Haversine.DistanceKm(waypoints[^1].Lat, waypoints[^1].Lon, destLat, destLon) > 5.0)
                path.Add((destLat, destLon));

            Console.WriteLine(
                $"[Navigraph] Airway snapped to {candidate.AirwayName}: " +
                $"{path.Count} points toward ({destLat:F2},{destLon:F2})");

            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Navigraph] GetAirwayPath: {ex.Message}");
            return null;
        }
    }

    // ── Candidate segment search ──────────────────────────────────────────────

    private record SegmentCandidate(
        string AirwayName, int FragmentNo, int SequenceNo,
        bool ForwardDirection, double Score);

    private SegmentCandidate? FindBestSegment(
        double acLat, double acLon, double acHeadingDeg,
        double destLat, double destLon)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();

        // Spatial bounding-box query using the pre-computed bbox columns
        cmd.CommandText = """
            SELECT airway_name, airway_fragment_no, sequence_no,
                   from_laty, from_lonx, to_laty, to_lonx
            FROM   airway
            WHERE  left_lonx   <= @lonMax
              AND  right_lonx  >= @lonMin
              AND  bottom_laty <= @latMax
              AND  top_laty    >= @latMin
            """;
        cmd.Parameters.AddWithValue("@latMin", acLat - SearchHalfDegLat);
        cmd.Parameters.AddWithValue("@latMax", acLat + SearchHalfDegLat);
        cmd.Parameters.AddWithValue("@lonMin", acLon - SearchHalfDegLon);
        cmd.Parameters.AddWithValue("@lonMax", acLon + SearchHalfDegLon);

        SegmentCandidate? best = null;

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            string name  = rdr.GetString(0);
            int    frag  = rdr.GetInt32(1);
            int    seq   = rdr.GetInt32(2);
            double fLat  = rdr.GetDouble(3);
            double fLon  = rdr.GetDouble(4);
            double tLat  = rdr.GetDouble(5);
            double tLon  = rdr.GetDouble(6);

            // Forward segment bearing
            double fwdBearing = FlyByArcHelper.BearingDeg(fLat, fLon, tLat, tLon);
            double revBearing = (fwdBearing + 180.0) % 360.0;

            // Check both directions
            foreach (var (bearing, forward) in new[] {(fwdBearing, true), (revBearing, false)})
            {
                double delta = HeadingDelta(acHeadingDeg, bearing);
                if (delta > HeadingToleranceDeg) continue;

                // The "to" point in this direction
                var (toLat, toLon) = forward ? (tLat, tLon) : (fLat, fLon);

                // Only accept segments that move us closer to the destination
                double distBefore = Haversine.DistanceKm(acLat, acLon, destLat, destLon);
                double distAfter  = Haversine.DistanceKm(toLat, toLon, destLat, destLon);
                if (distAfter >= distBefore) continue;

                // Score: lower heading delta + more progress toward dest = better
                double headingScore = 1.0 - delta / HeadingToleranceDeg;
                double progressKm   = distBefore - distAfter;
                double score        = headingScore * 2.0 + progressKm / 100.0;

                if (best is null || score > best.Score)
                    best = new SegmentCandidate(name, frag, seq, forward, score);
            }
        }

        return best;
    }

    // ── Airway following ──────────────────────────────────────────────────────

    private List<(double Lat, double Lon)> FollowAirway(
        string airwayName, int fragmentNo, int startSeq,
        bool forward, double destLat, double destLon)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT sequence_no, from_laty, from_lonx, to_laty, to_lonx
            FROM   airway
            WHERE  airway_name       = @name
              AND  airway_fragment_no = @frag
            ORDER BY sequence_no
            """;
        cmd.Parameters.AddWithValue("@name", airwayName);
        cmd.Parameters.AddWithValue("@frag", fragmentNo);

        // Load all segments of this airway fragment
        var segments = new List<(int Seq, double FLat, double FLon, double TLat, double TLon)>();
        using (var rdr = cmd.ExecuteReader())
        {
            while (rdr.Read())
                segments.Add((rdr.GetInt32(0), rdr.GetDouble(1), rdr.GetDouble(2),
                              rdr.GetDouble(3), rdr.GetDouble(4)));
        }

        if (segments.Count == 0) return [];

        // Find starting index
        int startIdx = segments.FindIndex(s => s.Seq == startSeq);
        if (startIdx < 0) startIdx = 0;

        // Build ordered list of "next point" in the travel direction
        var result = new List<(double Lat, double Lon)>();

        double prevDist = Haversine.DistanceKm(
            forward ? segments[startIdx].FLat : segments[startIdx].TLat,
            forward ? segments[startIdx].FLon : segments[startIdx].TLon,
            destLat, destLon);

        IEnumerable<int> indices = forward
            ? Enumerable.Range(startIdx, segments.Count - startIdx)
            : Enumerable.Range(0, startIdx + 1).Reverse();

        foreach (int i in indices)
        {
            var seg = segments[i];
            var (lat, lon) = forward ? (seg.TLat, seg.TLon) : (seg.FLat, seg.FLon);

            double dist = Haversine.DistanceKm(lat, lon, destLat, destLon);

            // Stop if we've passed the destination or the airway diverges significantly
            if (dist < StarCutoffKm) break;           // close enough — go direct from here
            if (dist > prevDist + 50.0) break;        // airway moving away from dest

            result.Add((lat, lon));
            prevDist = dist;
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;");
        conn.Open();
        return conn;
    }

    /// <summary>Returns the absolute angular difference between two headings (0–180°).</summary>
    private static double HeadingDelta(double h1, double h2)
    {
        double d = Math.Abs(h1 - h2) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }
}
