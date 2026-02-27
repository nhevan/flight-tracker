namespace FlightTracker.Models;

/// <summary>
/// A FlightState decorated with optional route, aircraft metadata, a photo URL,
/// AI-generated aircraft facts, and an inferred heading derived from GPS position delta.
/// All enrichment fields are null when the lookup has not yet completed
/// or the upstream API returned no data.
/// </summary>
public sealed record EnrichedFlightState(
    FlightState   State,
    FlightRoute?  Route,
    AircraftInfo? Aircraft,
    string?       PhotoUrl,
    string?       AircraftFacts,
    /// <summary>
    /// Heading derived by comparing the aircraft's GPS position across two consecutive
    /// polling cycles. Populated by Program.cs after the second poll for aircraft that
    /// do not broadcast <see cref="FlightState.HeadingDegrees"/> via ADS-B.
    /// Null on the first poll or when the aircraft moved less than 50 m (GPS jitter).
    /// </summary>
    double?       InferredHeadingDegrees = null
)
{
    /// <summary>
    /// Transponder-broadcast heading when available; GPS-inferred heading otherwise.
    /// Null only when neither source has data (first poll for aircraft with no ADS-B heading).
    /// Use this for all heading consumers except display code that needs to distinguish the source
    /// (e.g. the <c>~</c> prefix shown in the terminal table and Telegram message).
    /// </summary>
    public double? EffectiveHeading => State.HeadingDegrees ?? InferredHeadingDegrees;
}
