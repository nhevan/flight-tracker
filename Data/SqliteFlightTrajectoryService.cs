using FlightTracker.Configuration;
using FlightTracker.Models;
using FlightTracker.Services;
using Microsoft.Data.Sqlite;

namespace FlightTracker.Data;

/// <summary>
/// Records the flight path (lat/lon samples) of every Rotterdam arrival and departure.
/// Each flight visit gets a <c>FlightTrackingSessions</c> row; each 30-second poll appends
/// a <c>FlightTrajectoryPoints</c> row.  An in-memory dictionary provides O(1) per-poll
/// look-ups so the hot path never hits SQLite just to check <c>IsTracking</c>.
/// </summary>
public sealed class SqliteFlightTrajectoryService : IFlightTrajectoryService
{
    private readonly string _connectionString;

    // In-memory state — keyed by ICAO24 (case-insensitive)
    private readonly Dictionary<string, TrackingSession> _activeSessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteFlightTrajectoryService(AppSettings settings)
    {
        string rawPath = settings.DatabasePath;
        string dbPath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(Directory.GetCurrentDirectory(), rawPath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
    }

    // ── Schema & startup ──────────────────────────────────────────────────────

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS FlightTrackingSessions (
                SessionId    TEXT    PRIMARY KEY,
                Icao24       TEXT    NOT NULL,
                Callsign     TEXT,
                FlightType   TEXT    NOT NULL,
                OriginIata   TEXT,
                DestIata     TEXT,
                StartedAt    TEXT    NOT NULL,
                CompletedAt  TEXT,
                IsComplete   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_traj_sessions_icao24
                ON FlightTrackingSessions(Icao24);

            CREATE TABLE IF NOT EXISTS FlightTrajectoryPoints (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId      TEXT    NOT NULL,
                Icao24         TEXT    NOT NULL,
                RecordedAt     TEXT    NOT NULL,
                Latitude       REAL    NOT NULL,
                Longitude      REAL    NOT NULL,
                AltitudeMeters REAL,
                SpeedKmh       REAL,
                HeadingDegrees REAL
            );
            CREATE INDEX IF NOT EXISTS ix_traj_points_session_id
                ON FlightTrajectoryPoints(SessionId);
            CREATE INDEX IF NOT EXISTS ix_traj_points_icao24
                ON FlightTrajectoryPoints(Icao24);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Rehydrate any sessions that were still active when the process last stopped.
        await RehydrateActiveSessionsAsync(conn, ct);
    }

    private async Task RehydrateActiveSessionsAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, Icao24, Callsign, FlightType, StartedAt
            FROM   FlightTrackingSessions
            WHERE  IsComplete = 0
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session = new TrackingSession(
                SessionId:  reader.GetString(0),
                Icao24:     reader.GetString(1),
                Callsign:   reader.IsDBNull(2) ? null : reader.GetString(2),
                FlightType: reader.GetString(3),
                StartedAt:  DateTimeOffset.Parse(reader.GetString(4)));

            _activeSessions[session.Icao24] = session;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsTracking(string icao24) =>
        _activeSessions.ContainsKey(icao24);

    public IReadOnlyCollection<string> GetActiveIcaos() =>
        _activeSessions.Keys.ToList();

    public async Task StartOrContinueAsync(
        EnrichedFlightState flight,
        string flightType,
        CancellationToken ct = default)
    {
        var f = flight.State;
        if (f.Latitude is null || f.Longitude is null) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (!_activeSessions.TryGetValue(f.Icao24, out var session))
            {
                // Open a new session
                var now       = DateTimeOffset.UtcNow;
                var sessionId = $"{f.Icao24}_{now:yyyyMMddHHmmss}";

                session = new TrackingSession(
                    SessionId:  sessionId,
                    Icao24:     f.Icao24,
                    Callsign:   string.IsNullOrWhiteSpace(f.Callsign) ? null : f.Callsign.Trim(),
                    FlightType: flightType,
                    StartedAt:  now);

                _activeSessions[f.Icao24] = session;
                await PersistNewSessionAsync(session, flight, ct);
            }

            await AppendPointAsync(session.SessionId, f, flight.EffectiveHeading, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompleteSessionAsync(string icao24, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_activeSessions.TryGetValue(icao24, out var session))
                return;

            _activeSessions.Remove(icao24);
            await MarkSessionCompleteAsync(session.SessionId, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<(double Lat, double Lon)>> GetRecordedPointsAsync(
        string icao24,
        CancellationToken ct = default)
    {
        if (!_activeSessions.TryGetValue(icao24, out var session))
            return Array.Empty<(double, double)>();

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Latitude, Longitude
            FROM   FlightTrajectoryPoints
            WHERE  SessionId = @sessionId
            ORDER  BY RecordedAt ASC
            """;
        cmd.Parameters.AddWithValue("@sessionId", session.SessionId);

        var points = new List<(double, double)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add((reader.GetDouble(0), reader.GetDouble(1)));

        return points;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task PersistNewSessionAsync(
        TrackingSession session,
        EnrichedFlightState flight,
        CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FlightTrackingSessions
                (SessionId, Icao24, Callsign, FlightType, OriginIata, DestIata, StartedAt, IsComplete)
            VALUES
                (@sessionId, @icao24, @callsign, @flightType, @originIata, @destIata, @startedAt, 0)
            """;
        cmd.Parameters.AddWithValue("@sessionId",  session.SessionId);
        cmd.Parameters.AddWithValue("@icao24",     session.Icao24);
        cmd.Parameters.AddWithValue("@callsign",   (object?)session.Callsign   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@flightType", session.FlightType);
        cmd.Parameters.AddWithValue("@originIata", (object?)flight.Route?.OriginIata ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destIata",   (object?)flight.Route?.DestIata   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startedAt",  session.StartedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task AppendPointAsync(
        string sessionId,
        FlightState f,
        double? effectiveHeading,
        CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FlightTrajectoryPoints
                (SessionId, Icao24, RecordedAt, Latitude, Longitude, AltitudeMeters, SpeedKmh, HeadingDegrees)
            VALUES
                (@sessionId, @icao24, @recordedAt, @lat, @lon, @alt, @speed, @heading)
            """;
        cmd.Parameters.AddWithValue("@sessionId",  sessionId);
        cmd.Parameters.AddWithValue("@icao24",     f.Icao24);
        cmd.Parameters.AddWithValue("@recordedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@lat",        f.Latitude!.Value);
        cmd.Parameters.AddWithValue("@lon",        f.Longitude!.Value);
        cmd.Parameters.AddWithValue("@alt",        (object?)f.BarometricAltitudeMeters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@speed",      f.VelocityMetersPerSecond is double v
                                                       ? (object)(v * 3.6)
                                                       : DBNull.Value);
        cmd.Parameters.AddWithValue("@heading",    (object?)effectiveHeading ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkSessionCompleteAsync(string sessionId, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE FlightTrackingSessions
            SET    IsComplete  = 1,
                   CompletedAt = @completedAt
            WHERE  SessionId   = @sessionId
            """;
        cmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@sessionId",   sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
