namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IPredictedPathService
{
    /// <summary>
    /// Returns the predicted flight path ahead of the aircraft as an ordered list of
    /// WGS-84 lat/lon coordinates.  Returns null when FlightAware is not configured,
    /// the route is unknown, or no waypoints can be resolved from the local navdata.
    /// Never throws.
    /// </summary>
    Task<PredictedFlightPath?> GetPredictedPathAsync(
        EnrichedFlightState flight,
        CancellationToken cancellationToken);
}
