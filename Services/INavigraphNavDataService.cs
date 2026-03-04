namespace FlightTracker.Services;

using FlightTracker.Models;

/// <summary>
/// Provides fix resolution and airway-based path inference using the
/// Navigraph / Little NavMap SQLite database (little_navmap_navigraph.sqlite).
///
/// Fix resolution covers 200 000+ worldwide waypoints (much wider than the
/// local ARINC 424 file).
///
/// Airway snapping builds an ordered list of waypoints by:
///   1. Finding airway segments near the aircraft whose bearing aligns with
///      the aircraft's current heading.
///   2. Following the best-matching airway's ordered segment sequence toward
///      the destination.
///   3. Falling back to null when no aligned airway can be found.
/// </summary>
public interface INavigraphNavDataService
{
    /// <summary>
    /// Resolves a fix identifier to coordinates.
    /// When multiple fixes share the same name, returns the one closest to
    /// the hint position (aircraft's current lat/lon).
    /// </summary>
    NavFix? TryResolveFix(string name, double hintLat, double hintLon);

    /// <summary>
    /// Infers the enroute path from the aircraft's current position toward
    /// the destination by snapping to the nearest heading-aligned airway and
    /// following its ordered waypoints.
    /// Returns null when no suitable airway is found (caller should fall back
    /// to a direct origin→dest line).
    /// The returned list starts at the aircraft's current position and ends
    /// near the destination.
    /// </summary>
    List<(double Lat, double Lon)>? GetAirwayPath(
        double acLat, double acLon, double acHeadingDeg,
        double destLat, double destLon);
}
