namespace FlightTracker.Helpers;

using FlightTracker.Models;

/// <summary>
/// Computes fly-by turn arcs for FMS-style route path smoothing.
///
/// Fly-by arc geometry:
///   1. The aircraft begins turning <em>before</em> the waypoint (lead-in distance d).
///   2. A circular arc is drawn from the lead-in point to the roll-out point.
///   3. The arc radius is derived from true airspeed and a standard 25° bank angle.
///
/// All lat/lon values are WGS-84 decimal degrees.
/// All distances are metres unless otherwise noted.
/// </summary>
public static class FlyByArcHelper
{
    private const double G           = 9.80665;              // m/s²
    private const double BankRad     = 25.0 * Math.PI / 180; // 25° in radians
    private const double TanBank     = 0.46631;              // tan(25°)
    private const double MinTasMs    = 50.0 * 0.514444;      // 50 kt → m/s
    private const double MaxRadiusM  = 80_000.0;             // 80 km cap
    private const double EarthRadiusM = 6_371_000.0;

    /// <summary>
    /// Computes the standard 25° bank-angle turn radius in metres.
    /// </summary>
    public static double ComputeTurnRadiusM(double tasMs)
    {
        double effectiveTas = Math.Max(tasMs, MinTasMs);
        double r = effectiveTas * effectiveTas / (G * TanBank);
        return Math.Min(r, MaxRadiusM);
    }

    /// <summary>
    /// Distance before the waypoint at which the aircraft should begin turning.
    /// d = R × tan(Δθ / 2)
    /// </summary>
    public static double ComputeLeadInDistanceM(double radiusM, double turnAngleDeg)
    {
        double halfAngle = Math.Abs(turnAngleDeg) * Math.PI / 360.0; // Δθ/2 in radians
        return radiusM * Math.Tan(halfAngle);
    }

    /// <summary>
    /// Returns the great-circle bearing (degrees, 0–360) from point A to point B.
    /// </summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double φ1 = ToRad(lat1), φ2 = ToRad(lat2);
        double Δλ = ToRad(lon2 - lon1);
        double y  = Math.Sin(Δλ) * Math.Cos(φ2);
        double x  = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        return (ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>
    /// Projects a point <paramref name="distM"/> metres from a given origin along a bearing.
    /// </summary>
    public static (double Lat, double Lon) ProjectPoint(
        double lat, double lon, double bearingDeg, double distM)
    {
        double φ1  = ToRad(lat);
        double λ1  = ToRad(lon);
        double brng = ToRad(bearingDeg);
        double δ   = distM / EarthRadiusM;

        double φ2 = Math.Asin(
            Math.Sin(φ1) * Math.Cos(δ) +
            Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(brng));
        double λ2 = λ1 + Math.Atan2(
            Math.Sin(brng) * Math.Sin(δ) * Math.Cos(φ1),
            Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));

        return (ToDeg(φ2), ToDeg(λ2));
    }

    /// <summary>
    /// Samples a circular arc on the sphere into an ordered list of lat/lon points.
    /// The arc is centred at (<paramref name="cLat"/>, <paramref name="cLon"/>) with
    /// radius <paramref name="radiusM"/> metres, sweeping from <paramref name="startBrng"/>
    /// to <paramref name="endBrng"/> in the direction implied by the turn sense
    /// (<paramref name="rightTurn"/> = clockwise).
    /// </summary>
    public static List<(double Lat, double Lon)> SampleArc(
        double cLat, double cLon,
        double radiusM,
        double startBrng, double endBrng,
        bool   rightTurn,
        int    stepsPerRadian = 8)
    {
        // Normalise bearings so the sweep always goes in the right direction
        double sweep = rightTurn
            ? ((endBrng - startBrng + 360.0) % 360.0)
            : ((startBrng - endBrng + 360.0) % 360.0);

        int steps = Math.Max(2, (int)Math.Ceiling(sweep * Math.PI / 180.0 * stepsPerRadian));
        double stepDeg = sweep / steps * (rightTurn ? 1.0 : -1.0);

        var pts = new List<(double, double)>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double brng = (startBrng + i * stepDeg + 360.0) % 360.0;
            pts.Add(ProjectPoint(cLat, cLon, brng, radiusM));
        }
        return pts;
    }

    /// <summary>
    /// Builds the fly-by arc points at waypoint <c>W</c>, given the previous fix <c>P</c>
    /// and the next fix <c>N</c>.  Returns an empty list when the arc is too large for the
    /// segment lengths (very sharp turns or very short legs) — caller falls back to a
    /// straight line through <c>W</c>.
    /// </summary>
    /// <param name="tasMs">True airspeed in m/s (VelocityMetersPerSecond is already m/s).</param>
    /// <returns>Arc sample points from lead-in to roll-out, NOT including P or N themselves.</returns>
    public static List<(double Lat, double Lon)> BuildFlyByArc(
        double pLat, double pLon,   // previous fix
        double wLat, double wLon,   // current waypoint (turn point)
        double nLat, double nLon,   // next fix
        double tasMs)
    {
        double inboundBrng  = BearingDeg(pLat, pLon, wLat, wLon);
        double outboundBrng = BearingDeg(wLat, wLon, nLat, nLon);

        // Signed turn angle: positive = right, negative = left
        double turnAngle = ((outboundBrng - inboundBrng + 540.0) % 360.0) - 180.0;
        double absTurn   = Math.Abs(turnAngle);

        // No meaningful arc for nearly straight segments
        if (absTurn < 3.0 || absTurn > 175.0)
            return [];

        bool rightTurn = turnAngle > 0;
        double radiusM = ComputeTurnRadiusM(tasMs);
        double leadInM = ComputeLeadInDistanceM(radiusM, absTurn);

        // Sanity check: lead-in must fit within each segment
        double pwDistM = Haversine.DistanceKm(pLat, pLon, wLat, wLon) * 1000.0;
        double wnDistM = Haversine.DistanceKm(wLat, wLon, nLat, nLon) * 1000.0;
        if (leadInM >= pwDistM * 0.49 || leadInM >= wnDistM * 0.49)
            return [];

        // Arc start = project back along inbound bearing
        double reverseBrng = (inboundBrng + 180.0) % 360.0;
        var (arcStartLat, arcStartLon) = ProjectPoint(wLat, wLon, reverseBrng, leadInM);

        // Arc end = project forward along outbound bearing
        var (arcEndLat, arcEndLon) = ProjectPoint(wLat, wLon, outboundBrng, leadInM);

        // Arc centre = perpendicular from arc start, distance R
        double perpBrng = rightTurn
            ? (inboundBrng + 90.0)  % 360.0
            : (inboundBrng + 270.0) % 360.0;
        var (cLat, cLon) = ProjectPoint(arcStartLat, arcStartLon, perpBrng, radiusM);

        // Start and end bearings from centre to arc points
        double startBrngFromCenter = BearingDeg(cLat, cLon, arcStartLat, arcStartLon);
        double endBrngFromCenter   = BearingDeg(cLat, cLon, arcEndLat,   arcEndLon);

        return SampleArc(cLat, cLon, radiusM, startBrngFromCenter, endBrngFromCenter, rightTurn);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
