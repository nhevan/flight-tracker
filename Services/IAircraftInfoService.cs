namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IAircraftInfoService
{
    /// <summary>
    /// Returns static metadata for an aircraft by ICAO24 hex.
    /// Returns null when hexdb has no record for that transponder code.
    /// Result is cached indefinitely for the session lifetime.
    /// </summary>
    Task<AircraftInfo?> GetAircraftInfoAsync(string icao24, CancellationToken cancellationToken);
}
