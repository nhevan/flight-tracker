using FlightTracker.Models;

namespace FlightTracker.Data;

public interface IFlightLoggingService
{
    /// <summary>
    /// Creates the database and schema if they don't already exist.
    /// Must be called once on application startup before any other methods.
    /// </summary>
    Task InitialiseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a flight sighting to the database.
    /// Never throws — errors are logged to the console and silently swallowed.
    /// </summary>
    Task LogAsync(
        EnrichedFlightState flight,
        string direction,
        double? etaSeconds,
        double homeLat,
        double homeLon,
        string? homeName,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes aggregated stats from all recorded sightings at the given location.
    /// </summary>
    Task<FlightStats> GetStatsAsync(
        double homeLat,
        double homeLon,
        CancellationToken cancellationToken = default);
}
