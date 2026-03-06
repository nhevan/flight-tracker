namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightTrajectoryService
{
    /// <summary>Initialises the SQLite schema and rehydrates active sessions from the database.</summary>
    Task InitialiseAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true when there is an open tracking session for <paramref name="icao24"/>.
    /// </summary>
    bool IsTracking(string icao24);

    /// <summary>
    /// Opens a new session if none exists for <paramref name="icao24"/>, then records a
    /// trajectory point from the current flight state.  If a session is already open the
    /// new point is appended without creating a duplicate session (satisfies condition 5).
    /// </summary>
    Task StartOrContinueAsync(EnrichedFlightState flight, string flightType, CancellationToken ct = default);

    /// <summary>
    /// Marks the session for <paramref name="icao24"/> as complete (flight left range / landed).
    /// No-ops if no active session exists.
    /// </summary>
    Task CompleteSessionAsync(string icao24, CancellationToken ct = default);

    /// <summary>
    /// Returns all recorded sessions (both active and completed), ordered most-recent first.
    /// Each entry includes the callsign, route, flight type, start time, completion status,
    /// and total number of recorded trajectory points.
    /// </summary>
    Task<IReadOnlyList<RecordedSessionInfo>> GetAllSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all ICAO24 identifiers that currently have an open tracking session.
    /// Used each poll cycle to close sessions for flights that have left the range.
    /// </summary>
    IReadOnlyCollection<string> GetActiveIcaos();

    /// <summary>
    /// Returns all recorded lat/lon coordinates for the most recent session of
    /// <paramref name="icao24"/> that is still active, in chronological order.
    /// Returns an empty list if the flight is not being tracked.
    /// </summary>
    Task<IReadOnlyList<(double Lat, double Lon)>> GetRecordedPointsAsync(string icao24, CancellationToken ct = default);
}
