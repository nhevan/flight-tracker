namespace FlightTracker.Models;

/// <summary>
/// Prior sighting data returned by <see cref="Services.IRepeatVisitorService"/>.
/// Non-null only when the aircraft has been seen before (≥1 prior sighting in the database).
/// </summary>
public sealed record RepeatVisitorInfo(
    int            TotalPreviousSightings, // count of existing rows in FlightSightings for this Icao24
    DateTimeOffset LastSeenAt,             // timestamp of the most recent prior sighting
    string?        LastDestIata,           // DestIata from that sighting — may be null if route was unknown
    string?        LastOriginIata          // OriginIata from that sighting — may be null if route was unknown
);
