namespace FlightTracker.Models;

/// <summary>
/// Payload broadcast to all SSE subscribers when a flight triggers an overhead notification.
/// Fires under the same conditions as Telegram: ETA ≤ 120 s and altitude ≤ MaxAltitudeMeters.
/// </summary>
public sealed record FlightSseEvent(
    // ── Identity ──────────────────────────────────────────────────────────────
    string  Icao24,
    string  Callsign,

    // ── Position & motion ─────────────────────────────────────────────────────
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    double? SpeedKmh,
    double? HeadingDegrees,       // effective (broadcast or GPS-inferred)
    double? VerticalRateMetersPerSecond,

    // ── Proximity ────────────────────────────────────────────────────────────
    double? DistanceKm,
    double? EtaSeconds,
    string  Direction,            // "Overhead" | "Towards" | cardinal direction

    // ── Route ─────────────────────────────────────────────────────────────────
    string? OriginIata,
    string? OriginIcao,
    string? OriginName,
    string? DestIata,
    string? DestIcao,
    string? DestName,

    // ── Aircraft identity ─────────────────────────────────────────────────────
    string? AircraftDescription,  // e.g. "Boeing 787-9"
    string? TypeCode,             // ICAO type code e.g. "B789"
    string? Registration,
    string? Operator,

    /// <summary>
    /// Simplified category for UI icon/animation selection.
    /// One of: widebody-jet | narrowbody-jet | turboprop | helicopter | military | business-jet | light-aircraft | unknown
    /// </summary>
    string  PlaneTypeCategory,

    // ── Images ────────────────────────────────────────────────────────────────
    /// <summary>Real photo of this specific registration from planespotters.net. Null when unavailable.</summary>
    string? PhotoUrl,

    /// <summary>
    /// Type silhouette URL derived from TypeCode.
    /// Pattern: https://www.planespotters.net/silhouettes/{TypeCode}_3.png
    /// Null when TypeCode is unknown.
    /// </summary>
    string? SilhouetteUrl,

    // ── Flags ─────────────────────────────────────────────────────────────────
    bool    IsMilitary,
    bool    IsEmergency,
    string? Emergency,            // null | "general" | "lifeguard" | "minfuel" | "nordo" | "unlawful" | "downed"
    string? Squawk,
    bool    IsCourseChange,       // true when this is a re-notification due to a significant bearing change

    // ── Environmental ─────────────────────────────────────────────────────────
    double? WindSpeedKnots,
    double? WindDirectionDeg,
    double? OutsideAirTempC,

    // ── Timestamp ─────────────────────────────────────────────────────────────
    DateTimeOffset Timestamp
);
