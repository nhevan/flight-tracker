namespace FlightTracker.Models;

/// <summary>
/// Aggregated statistics derived from the FlightSightings SQLite table.
/// All nullable fields are null when there is insufficient data (e.g. no sightings yet).
/// </summary>
public sealed record FlightStats(
    /// <summary>Total number of flight sightings ever logged.</summary>
    int TotalSightings,

    /// <summary>Number of sightings logged today (local time).</summary>
    int TodayCount,

    /// <summary>Number of distinct aircraft (by ICAO24) seen today.</summary>
    int TodayUniqueAircraft,

    /// <summary>Hour of day (0–23, local time) with the most historical sightings.</summary>
    int? BusiestHour,

    /// <summary>Average sightings per day during the busiest hour.</summary>
    double? BusiestHourAvgPerDay,

    /// <summary>Airline / operator with the most sightings.</summary>
    string? MostSpottedAirline,

    /// <summary>Number of sightings for the most spotted airline.</summary>
    int? MostSpottedAirlineCount,

    /// <summary>ICAO aircraft type code seen the fewest times (min 1 sighting).</summary>
    string? RarestTypeCode,

    /// <summary>Number of sightings for the rarest aircraft type.</summary>
    int? RarestTypeCount,

    /// <summary>Longest recorded gap between two consecutive sightings.</summary>
    TimeSpan? LongestGap,

    /// <summary>Start of the longest gap (UTC).</summary>
    DateTimeOffset? LongestGapStart,

    /// <summary>End of the longest gap (UTC).</summary>
    DateTimeOffset? LongestGapEnd,

    /// <summary>
    /// How many consecutive hours (going backwards from now, local time) each
    /// contained at least one sighting. 0 if the current hour has no sightings.
    /// </summary>
    int CurrentStreakHours
);
