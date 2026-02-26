namespace FlightTracker.Models;

/// <summary>
/// A FlightState decorated with optional route, aircraft metadata, a photo URL,
/// and AI-generated aircraft facts.
/// All enrichment fields are null when the lookup has not yet completed
/// or the upstream API returned no data.
/// </summary>
public sealed record EnrichedFlightState(
    FlightState   State,
    FlightRoute?  Route,
    AircraftInfo? Aircraft,
    string?       PhotoUrl,
    string?       AircraftFacts
);
