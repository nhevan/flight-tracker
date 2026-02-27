using FlightTracker.Configuration;
using FlightTracker.Models;
using Microsoft.Data.Sqlite;

namespace FlightTracker.Data;

public sealed class SqliteFlightLoggingService : IFlightLoggingService
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public SqliteFlightLoggingService(AppSettings settings)
    {
        // Resolve relative paths from the working directory so the DB stays at a
        // stable location (project root locally; /opt/flighttracker on EC2).
        string rawPath = settings.DatabasePath;
        _dbPath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(Directory.GetCurrentDirectory(), rawPath);

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = $"Data Source={_dbPath}";
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS FlightSightings (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                SeenAt              TEXT    NOT NULL,
                Icao24              TEXT    NOT NULL,
                Callsign            TEXT,
                OriginCountry       TEXT,
                Latitude            REAL,
                Longitude           REAL,
                AltitudeMeters      REAL,
                VelocityKmh         REAL,
                HeadingDegrees      REAL,
                VerticalRateMps     REAL,
                DistanceKm          REAL,
                Direction           TEXT,
                EtaSeconds          REAL,
                TypeCode            TEXT,
                Registration        TEXT,
                Operator            TEXT,
                Category            TEXT,
                OriginIata          TEXT,
                DestIata            TEXT,
                RouteDistanceKm     REAL,
                Squawk              TEXT,
                Emergency           TEXT,
                IsMilitary          INTEGER,
                AltGeomMeters       REAL,
                NavAltitudeMeters   REAL,
                WindDirectionDeg    REAL,
                WindSpeedKnots      REAL,
                OutsideAirTempC     REAL,
                AircraftDesc        TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_sightings_seen_at ON FlightSightings(SeenAt);
            CREATE INDEX IF NOT EXISTS ix_sightings_icao24  ON FlightSightings(Icao24);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Migrate existing databases — add new columns if they don't exist yet.
        // ALTER TABLE ADD COLUMN fails if the column already exists; we catch and ignore that.
        var newColumns = new (string Name, string Type)[]
        {
            ("Squawk",            "TEXT"),
            ("Emergency",         "TEXT"),
            ("IsMilitary",        "INTEGER"),
            ("AltGeomMeters",     "REAL"),
            ("NavAltitudeMeters", "REAL"),
            ("WindDirectionDeg",  "REAL"),
            ("WindSpeedKnots",    "REAL"),
            ("OutsideAirTempC",   "REAL"),
            ("AircraftDesc",      "TEXT"),
        };
        foreach (var (name, type) in newColumns)
        {
            try
            {
                using var mc = conn.CreateCommand();
                mc.CommandText = $"ALTER TABLE FlightSightings ADD COLUMN {name} {type}";
                await mc.ExecuteNonQueryAsync(cancellationToken);
            }
            catch { /* column already exists — safe to ignore */ }
        }

        Console.WriteLine($"[FlightLog] Database ready: {_dbPath}");
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task LogAsync(
        EnrichedFlightState flight,
        string direction,
        double? etaSeconds,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var f = flight.State;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FlightSightings (
                    SeenAt, Icao24, Callsign, OriginCountry,
                    Latitude, Longitude, AltitudeMeters, VelocityKmh,
                    HeadingDegrees, VerticalRateMps, DistanceKm,
                    Direction, EtaSeconds,
                    TypeCode, Registration, Operator, Category,
                    OriginIata, DestIata, RouteDistanceKm,
                    Squawk, Emergency, IsMilitary,
                    AltGeomMeters, NavAltitudeMeters,
                    WindDirectionDeg, WindSpeedKnots, OutsideAirTempC,
                    AircraftDesc
                ) VALUES (
                    $seenAt, $icao24, $callsign, $originCountry,
                    $latitude, $longitude, $altitudeMeters, $velocityKmh,
                    $headingDegrees, $verticalRateMps, $distanceKm,
                    $direction, $etaSeconds,
                    $typeCode, $registration, $operator, $category,
                    $originIata, $destIata, $routeDistanceKm,
                    $squawk, $emergency, $isMilitary,
                    $altGeomMeters, $navAltitudeMeters,
                    $windDirectionDeg, $windSpeedKnots, $outsideAirTempC,
                    $aircraftDesc
                )
                """;

            cmd.Parameters.AddWithValue("$seenAt",           timestamp.UtcDateTime.ToString("o"));
            cmd.Parameters.AddWithValue("$icao24",           f.Icao24);
            cmd.Parameters.AddWithValue("$callsign",         f.Callsign.Trim() as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$originCountry",    f.OriginCountry as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$latitude",         f.Latitude as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$longitude",        f.Longitude as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$altitudeMeters",   f.BarometricAltitudeMeters as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$velocityKmh",      f.VelocityMetersPerSecond.HasValue
                                                                 ? f.VelocityMetersPerSecond.Value * 3.6
                                                                 : DBNull.Value);
            cmd.Parameters.AddWithValue("$headingDegrees",   f.HeadingDegrees as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verticalRateMps",  f.VerticalRateMetersPerSecond as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$distanceKm",       f.DistanceKm as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$direction",        direction as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$etaSeconds",       etaSeconds as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$typeCode",         flight.Aircraft?.TypeCode as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$registration",     flight.Aircraft?.Registration as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$operator",         flight.Aircraft?.Operator as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$category",         flight.Aircraft?.Category as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$originIata",       flight.Route?.OriginIata as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$destIata",         flight.Route?.DestIata as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$routeDistanceKm",  flight.Route?.RouteDistanceKm as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$squawk",           f.Squawk as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$emergency",        f.Emergency as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isMilitary",       f.IsMilitary ? 1 : DBNull.Value);
            cmd.Parameters.AddWithValue("$altGeomMeters",    f.AltGeomMeters as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$navAltitudeMeters",f.NavAltitudeMeters as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$windDirectionDeg", f.WindDirectionDeg as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$windSpeedKnots",   f.WindSpeedKnots as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outsideAirTempC",  f.OutsideAirTempC as object ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$aircraftDesc",     f.AircraftDescription as object ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FlightLog] Failed to log sighting: {ex.Message}");
        }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<FlightStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        int total              = await QueryScalarIntAsync(conn, "SELECT COUNT(*) FROM FlightSightings", cancellationToken);
        int todayCount         = await QueryScalarIntAsync(conn, "SELECT COUNT(*) FROM FlightSightings WHERE date(SeenAt,'localtime') = date('now','localtime')", cancellationToken);
        int todayUnique        = await QueryScalarIntAsync(conn, "SELECT COUNT(DISTINCT Icao24) FROM FlightSightings WHERE date(SeenAt,'localtime') = date('now','localtime')", cancellationToken);
        var busiestHour        = await GetBusiestHourAsync(conn, cancellationToken);
        var mostAirline        = await GetMostSpottedAirlineAsync(conn, cancellationToken);
        var rarestType         = await GetRarestTypeAsync(conn, cancellationToken);
        var longestGap         = await GetLongestGapAsync(conn, cancellationToken);
        int currentStreak      = await GetCurrentStreakAsync(conn, cancellationToken);

        return new FlightStats(
            TotalSightings:          total,
            TodayCount:              todayCount,
            TodayUniqueAircraft:     todayUnique,
            BusiestHour:             busiestHour?.Hour,
            BusiestHourAvgPerDay:    busiestHour?.AvgPerDay,
            MostSpottedAirline:      mostAirline?.Name,
            MostSpottedAirlineCount: mostAirline?.Count,
            RarestTypeCode:          rarestType?.TypeCode,
            RarestTypeCount:         rarestType?.Count,
            LongestGap:              longestGap?.Duration,
            LongestGapStart:         longestGap?.Start,
            LongestGapEnd:           longestGap?.End,
            CurrentStreakHours:      currentStreak);
    }

    // ── Private query helpers ─────────────────────────────────────────────────

    private static async Task<int> QueryScalarIntAsync(
        SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    private static async Task<(int Hour, double AvgPerDay)?> GetBusiestHourAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CAST(strftime('%H', SeenAt, 'localtime') AS INTEGER) as hour,
                   COUNT(*) as total,
                   COUNT(*) * 1.0 / NULLIF(COUNT(DISTINCT date(SeenAt,'localtime')), 0) as avg_per_day
            FROM FlightSightings
            GROUP BY hour
            ORDER BY total DESC
            LIMIT 1
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetInt32(0), reader.GetDouble(2));
        return null;
    }

    private static async Task<(string Name, int Count)?> GetMostSpottedAirlineAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Operator, COUNT(*) as cnt
            FROM FlightSightings
            WHERE Operator IS NOT NULL AND Operator != ''
            GROUP BY Operator
            ORDER BY cnt DESC
            LIMIT 1
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetString(0), reader.GetInt32(1));
        return null;
    }

    private static async Task<(string TypeCode, int Count)?> GetRarestTypeAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TypeCode, COUNT(*) as cnt
            FROM FlightSightings
            WHERE TypeCode IS NOT NULL AND TypeCode != ''
            GROUP BY TypeCode
            ORDER BY cnt ASC
            LIMIT 1
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetString(0), reader.GetInt32(1));
        return null;
    }

    private static async Task<(TimeSpan Duration, DateTimeOffset Start, DateTimeOffset End)?> GetLongestGapAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                MAX((julianday(b.SeenAt) - julianday(a.SeenAt)) * 86400) as gap_secs,
                a.SeenAt as gap_start,
                b.SeenAt as gap_end
            FROM FlightSightings a
            JOIN FlightSightings b
              ON b.Id = (SELECT MIN(Id) FROM FlightSightings WHERE Id > a.Id)
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
        {
            double gapSecs  = reader.GetDouble(0);
            string startStr = reader.GetString(1);
            string endStr   = reader.GetString(2);

            if (DateTimeOffset.TryParse(startStr, out var start) &&
                DateTimeOffset.TryParse(endStr,   out var end))
            {
                return (TimeSpan.FromSeconds(gapSecs), start, end);
            }
        }
        return null;
    }

    /// <summary>
    /// Counts consecutive hours going backwards from the current hour (local time)
    /// that each contain at least one sighting.
    /// </summary>
    private static async Task<int> GetCurrentStreakAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        // Fetch distinct hour-buckets in descending order
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT strftime('%Y-%m-%d %H', SeenAt, 'localtime') as hour_bucket
            FROM FlightSightings
            GROUP BY hour_bucket
            ORDER BY hour_bucket DESC
            LIMIT 200
            """;

        var hourBuckets = new List<DateTime>();
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (DateTime.TryParseExact(
                    reader.GetString(0), "yyyy-MM-dd HH",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var dt))
                {
                    hourBuckets.Add(dt);
                }
            }
        }

        if (hourBuckets.Count == 0) return 0;

        // Start from the current hour and walk backwards
        DateTime currentHour = DateTime.Now.Date.AddHours(DateTime.Now.Hour);
        int streak = 0;
        DateTime expected = currentHour;

        foreach (var bucket in hourBuckets)
        {
            if (bucket == expected)
            {
                streak++;
                expected = expected.AddHours(-1);
            }
            else if (bucket < expected)
            {
                break; // Gap found — streak ends
            }
        }

        return streak;
    }
}
