namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IPredictedPathService
{
    /// <summary>
    /// Returns the predicted flight path ahead of the aircraft as an ordered list of
    /// WGS-84 lat/lon coordinates.  Returns null when no route data is available or
    /// no waypoints can be resolved from the local navdata.
    /// Never throws.
    /// </summary>
    Task<PredictedFlightPath?> GetPredictedPathAsync(
        EnrichedFlightState flight,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a callsign from the session cache so the next call to
    /// <see cref="GetPredictedPathAsync"/> triggers a fresh computation.
    /// </summary>
    void InvalidateCache(string callsign);
}
