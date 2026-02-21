namespace FlightTracker.Models;

/// <summary>
/// Static metadata about an aircraft from hexdb.io, keyed by ICAO24 hex address.
/// All fields are nullable because hexdb may not have a record for every transponder code.
/// </summary>
public sealed record AircraftInfo(
    string? TypeCode,
    string? Registration,
    string? Operator,
    string? Category   // e.g. "Helicopter", "Narrow-body Jet", "Turboprop", "Light Aircraft"
);
