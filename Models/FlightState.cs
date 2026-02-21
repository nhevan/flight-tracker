namespace FlightTracker.Models;

public sealed class FlightState
{
    public string Icao24 { get; init; } = string.Empty;
    public string Callsign { get; init; } = string.Empty;
    public string OriginCountry { get; init; } = string.Empty;
    public double? Longitude { get; init; }
    public double? Latitude { get; init; }
    public double? BarometricAltitudeMeters { get; init; }
    public bool OnGround { get; init; }
    public double? VelocityMetersPerSecond { get; init; }
    public double? HeadingDegrees { get; init; }
    public double? VerticalRateMetersPerSecond { get; init; }
}

// Internal: represents the raw JSON envelope from OpenSky.
// States is JsonElement[][] because each flight is a positional heterogeneous array.
internal sealed class OpenSkyResponse
{
    public long Time { get; set; }
    public System.Text.Json.JsonElement[][]? States { get; set; }
}

// Internal: represents the OAuth2 token response from the OpenSky auth server.
internal sealed class TokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}
