namespace FlightTracker.Models;

/// <summary>
/// Origin and destination airports for a flight, sourced from adsbdb.com.
/// All fields are nullable — adsbdb may not know the route for every callsign.
/// RouteDistanceKm is the great-circle distance between the two airports (Haversine),
/// used to compute an approximate total flight time when combined with ground speed.
/// </summary>
public sealed record FlightRoute(
    string? OriginIcao,
    string? OriginIata,
    string? OriginName,
    double? OriginLat,
    double? OriginLon,
    string? DestIcao,
    string? DestIata,
    string? DestName,
    double? DestLat,
    double? DestLon,
    double? RouteDistanceKm   // Haversine(origin, dest); null if either airport lacks coordinates
);
