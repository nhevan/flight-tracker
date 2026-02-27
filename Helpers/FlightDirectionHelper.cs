namespace FlightTracker.Helpers;

using FlightTracker.Models;

/// <summary>
/// Pure-logic helper that classifies a flight's direction relative to a home position.
/// Shared between FlightTableRenderer (display) and TelegramNotificationService (alerts).
/// </summary>
public static class FlightDirectionHelper
{
    /// <summary>
    /// Returns one of: "Overhead", "Towards", "Away", "Crossing".
    /// Returns null when position or heading data is unavailable.
    /// </summary>
    public static string? Classify(
        double? lat, double? lon, double? heading, double? distKm,
        double homeLat, double homeLon)
    {
        // Within 5 km — essentially directly above regardless of heading
        if (distKm is <= 5.0)
            return "Overhead";

        if (lat is null || lon is null || heading is null)
            return null;

        // Bearing from flight position → home (degrees clockwise from north)
        double dLon  = ToRad(homeLon - lon.Value);
        double fLat  = ToRad(lat.Value);
        double hLat  = ToRad(homeLat);
        double y     = Math.Sin(dLon) * Math.Cos(hLat);
        double x     = Math.Cos(fLat) * Math.Sin(hLat) - Math.Sin(fLat) * Math.Cos(hLat) * Math.Cos(dLon);
        double bearingToHome = (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;

        // Angular difference folded to [0°, 180°]
        double diff = Math.Abs((heading.Value - bearingToHome + 360.0) % 360.0);
        if (diff > 180.0) diff = 360.0 - diff;

        return diff switch
        {
            <= 30  => "Towards",
            >= 150 => "Away",
            _      => "Crossing"
        };
    }

    /// <summary>
    /// Returns the seconds until the flight reaches its closest approach point to home
    /// (i.e. the moment it is most directly overhead), or null if the closest approach
    /// is already behind the flight or required data is missing.
    ///
    /// Uses a flat-earth equirectangular projection centred on the home position —
    /// error is less than 0.1 % for distances under 100 km.
    /// </summary>
    public static double? EtaToOverheadSeconds(
        double? lat, double? lon, double? heading, double? speedMs,
        double homeLat, double homeLon)
    {
        if (lat is null || lon is null || heading is null || speedMs is null || speedMs <= 0)
            return null;

        const double MetresPerDegree = 111_320.0;
        double scale = Math.Cos(lat.Value * Math.PI / 180.0); // longitude compression

        // Flight position relative to home in metres (east = +x, north = +y)
        double dx = (lon.Value - homeLon) * scale * MetresPerDegree;
        double dy = (lat.Value  - homeLat) * MetresPerDegree;

        // Heading unit vector (clockwise-from-north → standard x/y)
        double headRad = heading.Value * Math.PI / 180.0;
        double hx = Math.Sin(headRad);
        double hy = Math.Cos(headRad);

        // Scalar projection of (home − flight) onto heading vector
        // = distance along the current track to the closest approach point
        double t = (-dx) * hx + (-dy) * hy;

        // t > 0 means the closest approach is ahead; t ≤ 0 means it's already passed
        return t > 0 ? t / speedMs.Value : null;
    }

    /// <summary>
    /// Computes the initial bearing (degrees clockwise from north) from a previous GPS
    /// position to the current position — used as a fallback when an aircraft does not
    /// broadcast <c>HeadingDegrees</c> via its ADS-B transponder.
    /// Returns <c>null</c> when the two points are less than 50 metres apart
    /// (GPS jitter guard — not enough movement to infer a reliable heading).
    /// </summary>
    /// <param name="prevLat">Latitude of the aircraft's position on the previous poll.</param>
    /// <param name="prevLon">Longitude of the aircraft's position on the previous poll.</param>
    /// <param name="currLat">Latitude of the aircraft's current position.</param>
    /// <param name="currLon">Longitude of the aircraft's current position.</param>
    public static double? InferHeading(
        double prevLat, double prevLon,
        double currLat, double currLon)
    {
        if (Haversine.DistanceKm(prevLat, prevLon, currLat, currLon) * 1000.0 < 50.0)
            return null;

        double dLon    = ToRad(currLon - prevLon);
        double fromLat = ToRad(prevLat);
        double toLat   = ToRad(currLat);
        double y       = Math.Sin(dLon) * Math.Cos(toLat);
        double x       = Math.Cos(fromLat) * Math.Sin(toLat)
                       - Math.Sin(fromLat) * Math.Cos(toLat) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
