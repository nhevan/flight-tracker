namespace FlightTracker.Models;

/// <summary>
/// A FlightState decorated with optional route and aircraft metadata.
/// Both enrichment fields are null when the lookup has not yet completed
/// or the upstream API returned no data.
/// </summary>
public sealed record EnrichedFlightState(
    FlightState   State,
    FlightRoute?  Route,
    AircraftInfo? Aircraft
);
