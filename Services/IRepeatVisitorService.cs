namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IRepeatVisitorService
{
    /// <summary>
    /// Returns prior sighting data for the given ICAO24 hex code.
    /// Returns <c>null</c> when the aircraft has never been seen before, or when
    /// <paramref name="icao24"/> is null or empty (graceful skip).
    /// Must be called BEFORE the current sighting is logged so the returned count
    /// reflects only prior visits, not the one being processed now.
    /// </summary>
    Task<RepeatVisitorInfo?> GetVisitorInfoAsync(string? icao24, CancellationToken ct);
}
