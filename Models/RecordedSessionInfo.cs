namespace FlightTracker.Models;

/// <summary>
/// Represents a single recorded trajectory session returned by
/// <c>IFlightTrajectoryService.GetAllSessionsAsync()</c>.
/// </summary>
public sealed record RecordedSessionInfo(
    string SessionId,
    string Icao24,
    string? Callsign,
    /// <summary>"Arriving" or "Departing"</summary>
    string FlightType,
    string? OriginIata,
    string? DestIata,
    DateTimeOffset StartedAt,
    bool IsComplete,
    int PointCount);
