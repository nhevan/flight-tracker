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
    Task<byte[]?> GetSnapshotAsync(
        double? lat,
        double? lon,
        double? headingDegrees,
        double? inferredHeadingDegrees,
        double? altitudeMeters,
        CancellationToken cancellationToken);
}
