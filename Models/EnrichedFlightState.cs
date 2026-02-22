namespace FlightTracker.Models;

/// <summary>
/// A FlightState decorated with optional route, aircraft metadata, and a photo URL.
/// All enrichment fields are null when the lookup has not yet completed
/// or the upstream API returned no data.
/// </summary>
public sealed record EnrichedFlightState(
    FlightState   State,
    FlightRoute?  Route,
    AircraftInfo? Aircraft,
    string?       PhotoUrl
);
