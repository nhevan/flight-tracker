namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightService
{
    Task<IReadOnlyList<FlightState>> GetOverheadFlightsAsync(CancellationToken cancellationToken);
}
