namespace FlightTracker.Models;

/// <summary>
/// Represents an active trajectory-recording session for a single Rotterdam
/// arrival or departure.  Held in memory for fast per-poll look-ups; mirrored
/// to SQLite for persistence.
/// </summary>
public sealed record TrackingSession(
    string SessionId,
    string Icao24,
    string? Callsign,
    /// <summary>"Arriving" or "Departing"</summary>
    string FlightType,
    DateTimeOffset StartedAt);
