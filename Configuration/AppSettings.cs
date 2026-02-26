namespace FlightTracker.Configuration;

public sealed class AppSettings
{
    public HomeLocationSettings HomeLocation { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
    public OpenSkySettings OpenSky { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public AnthropicSettings Anthropic { get; set; } = new();
    public MapboxSettings Mapbox { get; set; } = new();
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

public sealed class TelegramSettings
{
    /// <summary>Set to true to enable Telegram notifications.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Bot token from @BotFather (e.g. "123456:ABC-DEF...").</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Your personal chat ID. Get it by sending a message to your bot then calling
    /// https://api.telegram.org/bot{TOKEN}/getUpdates and reading update.message.chat.id
    /// </summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Only notify for flights at or below this barometric altitude (metres). Default 3000 m.</summary>
    public double MaxAltitudeMeters { get; set; } = 3000.0;
}

public sealed class AnthropicSettings
{
    /// <summary>Set to true to enable AI-generated aircraft facts in Telegram messages.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Anthropic API key (get from console.anthropic.com).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model to use. Defaults to claude-haiku-4-5 for speed and low cost.</summary>
    public string Model { get; set; } = "claude-haiku-4-5";

    /// <summary>Maximum tokens in the AI response. 200 is plenty for 2-3 sentences of facts.</summary>
    public int MaxTokens { get; set; } = 200;
}

public sealed class MapboxSettings
{
    /// <summary>Set to true to include a live map image in Telegram notifications.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Mapbox access token (get from account.mapbox.com).</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Mapbox map style. Options: mapbox/dark-v11 (default), mapbox/satellite-v9, mapbox/streets-v12
    /// </summary>
    public string Style { get; set; } = "mapbox/dark-v11";
}
