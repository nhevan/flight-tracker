namespace FlightTracker.Services;

public interface IAircraftPhotoService
{
    /// <summary>
    /// Returns the URL of a photo for the given aircraft, or null if none found.
    /// Tries ICAO24 hex lookup first, then falls back to registration if available.
    /// Never throws — errors are swallowed to keep the tracker alive.
    /// </summary>
    Task<string?> GetPhotoUrlAsync(string icao24, string? registration, CancellationToken cancellationToken);
}
