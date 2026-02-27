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

    // ── Additional fields from airplanes.live ────────────────────────────────
    public string?  Squawk              { get; init; }  // Mode A transponder code e.g. "7700"
    public string?  Emergency           { get; init; }  // "none" | "general" | "lifeguard" | "minfuel" | "nordo" | "unlawful" | "downed"
    public bool     IsMilitary          { get; init; }  // true when dbFlags bit 1 is set
    public double?  AltGeomMeters       { get; init; }  // Geometric (GPS) altitude in metres
    public double?  NavAltitudeMeters   { get; init; }  // Autopilot MCP-selected altitude in metres
    public double?  WindDirectionDeg    { get; init; }  // Calculated wind direction (degrees)
    public double?  WindSpeedKnots      { get; init; }  // Calculated wind speed (knots)
    public double?  OutsideAirTempC     { get; init; }  // Calculated outside air temperature (°C)
    public string?  AircraftDescription { get; init; }  // Aircraft description e.g. "Boeing 787-9"
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

// Internal: represents the raw JSON envelope from airplanes.live /v2/point/{lat}/{lon}/{radius}
internal sealed class AirplanesLiveResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("ac")]
    public AirplanesLiveAircraft[]? Ac { get; set; }
}

internal sealed class AirplanesLiveAircraft
{
    [System.Text.Json.Serialization.JsonPropertyName("hex")]
    public string? Hex { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("flight")]
    public string? Flight { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lon")]
    public double? Lon { get; set; }

    // alt_baro is an integer (feet) when airborne, or the string "ground" when on the ground
    [System.Text.Json.Serialization.JsonPropertyName("alt_baro")]
    public System.Text.Json.JsonElement AltBaro { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("gs")]
    public double? Gs { get; set; }       // ground speed, knots

    [System.Text.Json.Serialization.JsonPropertyName("track")]
    public double? Track { get; set; }    // true track, degrees

    [System.Text.Json.Serialization.JsonPropertyName("baro_rate")]
    public double? BaroRate { get; set; } // vertical rate, ft/min

    [System.Text.Json.Serialization.JsonPropertyName("squawk")]
    public string? Squawk { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("emergency")]
    public string? Emergency { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("dbFlags")]
    public int DbFlags { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alt_geom")]
    public double? AltGeom { get; set; }              // geometric altitude, feet

    [System.Text.Json.Serialization.JsonPropertyName("nav_altitude_mcp")]
    public double? NavAltitudeMcp { get; set; }       // MCP-selected altitude, feet

    [System.Text.Json.Serialization.JsonPropertyName("wd")]
    public double? Wd { get; set; }                   // wind direction, degrees

    [System.Text.Json.Serialization.JsonPropertyName("ws")]
    public double? Ws { get; set; }                   // wind speed, knots

    [System.Text.Json.Serialization.JsonPropertyName("oat")]
    public double? Oat { get; set; }                  // outside air temp, °C

    [System.Text.Json.Serialization.JsonPropertyName("desc")]
    public string? Desc { get; set; }                 // aircraft description e.g. "Boeing 787-9"
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

    [System.Text.Json.Serialization.JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
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
