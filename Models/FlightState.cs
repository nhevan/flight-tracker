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

    /// <summary>Straight-line surface distance from home to the flight's reported position, in km.</summary>
    public double? DistanceKm { get; init; }
}

public static class Haversine
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>Returns the great-circle distance in km between two WGS-84 coordinates.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
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
