namespace FlightTracker.Services;

public interface IMapSnapshotService
{
    /// <summary>
    /// Fetches a static map PNG showing the aircraft's position and heading trajectory.
    /// Returns null when the feature is disabled, lat/lon are missing, or the request fails.
    /// Never throws.
    /// </summary>
    /// <param name="headingDegrees">Heading broadcast by the aircraft's ADS-B transponder, or null.</param>
    /// <param name="inferredHeadingDegrees">
    /// Fallback heading derived from GPS position delta across two polls.
    /// Used when <paramref name="headingDegrees"/> is null.
    /// </param>
    /// <param name="recordedDots">
    /// Previously-recorded lat/lon positions for this aircraft (from <c>FlightTrajectoryPoints</c>).
    /// When provided, small purple dots are overlaid on the map at each location.
    /// </param>
    Task<byte[]?> GetSnapshotAsync(
        double? lat,
        double? lon,
        double? headingDegrees,
        double? inferredHeadingDegrees,
        double? altitudeMeters,
        CancellationToken cancellationToken,
        IReadOnlyList<(double Lat, double Lon)>? trajectory = null,
        IReadOnlyList<(double Lat, double Lon)>? predictedPath = null,
        IReadOnlyList<(double Lat, double Lon)>? recordedDots = null);

    /// <summary>
    /// Fetches a static map PNG showing only the recorded dot positions for a known flight.
    /// The map is centred on the centroid of the dots with auto-zoom to fit all points.
    /// Returns null when the feature is disabled, <paramref name="dots"/> is empty, or the request fails.
    /// Never throws.
    /// </summary>
    Task<byte[]?> GetDotsSnapshotAsync(
        string callsign,
        IReadOnlyList<(double Lat, double Lon)> dots,
        CancellationToken cancellationToken);
}
