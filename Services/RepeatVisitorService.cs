using FlightTracker.Configuration;
using FlightTracker.Models;
using Microsoft.Data.Sqlite;

namespace FlightTracker.Services;

public sealed class RepeatVisitorService : IRepeatVisitorService
{
    private readonly string _connectionString;

    public RepeatVisitorService(AppSettings settings)
    {
        // Mirror the same path resolution used by SqliteFlightLoggingService so
        // both services point at the same database file.
        string rawPath = settings.DatabasePath;
        string dbPath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(Directory.GetCurrentDirectory(), rawPath);

        _connectionString = $"Data Source={dbPath}";
    }

    /// <inheritdoc/>
    public async Task<RepeatVisitorInfo?> GetVisitorInfoAsync(string? icao24, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(icao24))
            return null;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // A single query: subquery counts all prior rows, outer query pulls the
            // most recent one's route fields.  LIMIT 1 ensures at most one row back.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM FlightSightings WHERE Icao24 = @icao) AS Total,
                    SeenAt,
                    DestIata,
                    OriginIata
                FROM FlightSightings
                WHERE Icao24 = @icao
                ORDER BY SeenAt DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@icao", icao24);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
                return null; // no rows → first sighting

            int total = reader.GetInt32(0);
            if (total == 0)
                return null; // defensive: shouldn't happen if we got a row, but be safe

            // SeenAt is stored as ISO-8601 TEXT by SQLite
            string seenAtRaw = reader.GetString(1);
            DateTimeOffset lastSeenAt = DateTimeOffset.Parse(seenAtRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);

            string? lastDest   = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? lastOrigin = reader.IsDBNull(3) ? null : reader.GetString(3);

            return new RepeatVisitorInfo(total, lastSeenAt, lastDest, lastOrigin);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RepeatVisitor] Query failed for {icao24}: {ex.Message}");
            return null; // non-fatal — skip repeat detection rather than crashing
        }
    }
}
