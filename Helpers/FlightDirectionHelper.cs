namespace FlightTracker.Helpers;

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

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
