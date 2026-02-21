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

// Internal: adsbdb.com /v0/callsign/{callsign} response.
// Unknown callsign returns { "response": "invalid callsign: ..." } — FlightRoute will be null.
internal sealed class AdsbdbCallsignResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("response")]
    public AdsbdbFlightRoute? Response { get; set; }
}

internal sealed class AdsbdbFlightRoute
{
    [System.Text.Json.Serialization.JsonPropertyName("flightroute")]
    public AdsbdbRouteDetail? Flightroute { get; set; }
}

internal sealed class AdsbdbRouteDetail
{
    [System.Text.Json.Serialization.JsonPropertyName("origin")]
    public AdsbdbAirport? Origin { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("destination")]
    public AdsbdbAirport? Destination { get; set; }
}

internal sealed class AdsbdbAirport
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("iata_code")]
    public string? IataCode { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("icao_code")]
    public string? IcaoCode { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("country_name")]
    public string? CountryName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("municipality")]
    public string? Municipality { get; set; }
}

// Internal: root response from hexdb.io /api/v1/aircraft/{hex}
internal sealed class HexDbAircraftResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("ICAOTypeCode")]
    public string? ICAOTypeCode { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("Registration")]
    public string? Registration { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("RegisteredOwners")]
    public string? RegisteredOwners { get; set; }
}
