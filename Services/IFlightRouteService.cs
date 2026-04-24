namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightRouteService
{
    /// <summary>
    /// Returns the origin/destination route for a flight identified by its callsign.
    /// When <paramref name="lat"/>/<paramref name="lon"/> are supplied, the primary source
    /// (adsb.lol /routeset) uses the aircraft position to disambiguate schedules that share
    /// a callsign — which directly attacks the "wrong airport" failure mode. Falls back to
    /// adsbdb.com when the primary has no result. Returns null when neither source knows
    /// the route.
    /// Positive results are cached for the session; negative results are cached briefly so
    /// a plane that couldn't be disambiguated near departure gets re-tried with a better
    /// position later.
    /// </summary>
    Task<FlightRoute?> GetRouteAsync(
        string callsign,
        double? lat,
        double? lon,
        CancellationToken cancellationToken);
}
