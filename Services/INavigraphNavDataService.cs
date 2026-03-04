namespace FlightTracker.Services;

using FlightTracker.Models;

/// <summary>
/// Result returned by <see cref="INavigraphNavDataService.GetAirwayPath"/>.
/// </summary>
public sealed record AirwayPathResult(
    /// <summary>Ordered waypoints starting at the aircraft, ending near destination.</summary>
    List<(double Lat, double Lon)> Points,
    /// <summary>Name of the first (or only) airway snapped to — kept for compatibility.</summary>
    string AirwayName,
    /// <summary>Total number of airway segments examined across all bounding-box queries.</summary>
    int SegmentsScanned,
    /// <summary>Ordered list of every airway name used in the chained path (e.g. ["UY131","Z319","UL194","UN860"]).</summary>
    IReadOnlyList<string> AirwaysUsed
);

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
    /// True when the Navigraph SQLite database file was found and contains
    /// airway data. False when the file is missing — all airway lookups will
    /// return null and flights will fall back to direct paths.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Resolves a fix identifier to coordinates.
    /// When multiple fixes share the same name, returns the one closest to
    /// the hint position (aircraft's current lat/lon).
    /// </summary>
    NavFix? TryResolveFix(string name, double hintLat, double hintLon);

    /// <summary>
    /// Infers the enroute path from the aircraft's current position toward
    /// the destination by snapping to the nearest airway whose bearing aligns
    /// with the great-circle direction from the aircraft toward the destination,
    /// then following its ordered waypoints.
    /// Returns null when no suitable airway is found (caller should fall back
    /// to a direct origin→dest line).
    /// The returned path starts at the aircraft's current position and ends
    /// near the destination.
    /// </summary>
    AirwayPathResult? GetAirwayPath(
        double acLat, double acLon,
        double destLat, double destLon);
}
