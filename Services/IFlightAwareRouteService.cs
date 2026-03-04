namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightAwareRouteService
{
    /// <summary>
    /// Returns the filed route string for a flight (e.g. "BERGI1A ARNEM UL851 BEGAR DCT LOGAN LOGA2R").
    /// Returns null when FlightAware is disabled, no API key is configured, the flight
    /// is unknown, or the route field is empty.
    /// Results are cached per callsign for the session lifetime.
    /// </summary>
    Task<FiledRoute?> GetFiledRouteAsync(string callsign, CancellationToken cancellationToken);
}
