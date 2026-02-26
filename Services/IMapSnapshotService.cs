namespace FlightTracker.Services;

public interface IMapSnapshotService
{
    /// <summary>
    /// Fetches a static map PNG showing the aircraft's position and heading trajectory.
    /// Returns null when the feature is disabled, lat/lon are missing, or the request fails.
    /// Never throws.
    /// </summary>
    Task<byte[]?> GetSnapshotAsync(
        double? lat,
        double? lon,
        double? headingDegrees,
        double? altitudeMeters,
        CancellationToken cancellationToken);
}
