namespace FlightTracker.Models;

/// <summary>
/// Origin and destination airports for a flight, sourced from adsbdb.com.
/// All fields are nullable — adsbdb may not know the route for every callsign.
/// </summary>
public sealed record FlightRoute(
    string? OriginIcao,
    string? OriginIata,
    string? OriginName,
    string? DestIcao,
    string? DestIata,
    string? DestName
);
