namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightRouteService
{
    /// <summary>
    /// Returns the origin/destination route for a flight identified by its callsign.
    /// Returns null when adsbdb has no record for this callsign.
    /// Results are cached for the session lifetime — scheduled routes don't change mid-flight.
    /// </summary>
    Task<FlightRoute?> GetRouteAsync(string callsign, CancellationToken cancellationToken);
}
