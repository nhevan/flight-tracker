namespace FlightTracker.Configuration;

public sealed class AppSettings
{
    public HomeLocationSettings HomeLocation { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
    public OpenSkySettings OpenSky { get; set; } = new();
}

public sealed class HomeLocationSettings
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double BoundingBoxDegrees { get; set; } = 1.0;

    /// <summary>
    /// Only show flights within this many km of home (visual range filter).
    /// Set to 0 to disable filtering and show everything in the bounding box.
    /// A typical visual range on a clear day is 30–50 km.
    /// </summary>
    public double VisualRangeKm { get; set; } = 50.0;
}

public sealed class PollingSettings
{
    public int IntervalSeconds { get; set; } = 30;
}

public sealed class OpenSkySettings
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://opensky-network.org/api/";
    public string TokenUrl { get; set; } =
        "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token";
}
