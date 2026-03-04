namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightService
{
    Task<IReadOnlyList<FlightState>> GetOverheadFlightsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a single aircraft by its callsign in the live ADS-B feed.
    /// Returns null when the callsign is not currently tracked.
    /// </summary>
    Task<FlightState?> GetFlightByCallsignAsync(string callsign, CancellationToken cancellationToken);
}
