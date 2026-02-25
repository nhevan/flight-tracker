# Flight Tracker

A .NET 10 console app that monitors aircraft flying over your home in real time. It displays a live terminal table with flight data and sends Telegram notifications when a low-altitude aircraft is approaching overhead.

## Features

- Live terminal table showing all flights within your configured range
- Distance, route, aircraft type, altitude, speed, heading, and ETA to closest approach
- Audible alert (bell) when a new flight enters the area
- Telegram notifications (with aircraft photo) when a flight is ≤ 2 minutes from overhead and below your altitude threshold
- Graceful shutdown on Ctrl+C or terminal close

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An [OpenSky Network](https://opensky-network.org) account with API credentials
- (Optional) A Telegram bot token and chat ID for notifications

## Setup

1. Copy `appsettings.example.json` to `appsettings.json`
2. Fill in your credentials and home location (see [Configuration](#configuration) below)
3. Build and run:

```bash
dotnet build
dotnet run
```

Press **Ctrl+C** to stop.

## Configuration

All settings live in `appsettings.json` (gitignored — never committed).

```json
{
  "HomeLocation": {
    "Latitude": 51.9836,
    "Longitude": 4.6311,
    "BoundingBoxDegrees": 1.0,
    "VisualRangeKm": 20
  },
  "Polling": {
    "IntervalSeconds": 30
  },
  "OpenSky": {
    "ClientId": "your_client_id",
    "ClientSecret": "your_client_secret",
    "BaseUrl": "https://opensky-network.org/api/",
    "TokenUrl": "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token"
  },
  "Telegram": {
    "Enabled": false,
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID",
    "MaxAltitudeMeters": 3000
  }
}
```

### HomeLocation

| Field | Default | Description |
|-------|---------|-------------|
| `Latitude` | — | Home latitude (WGS-84) |
| `Longitude` | — | Home longitude (WGS-84) |
| `BoundingBoxDegrees` | `1.0` | Size of the query area (±degrees from center) |
| `VisualRangeKm` | `50.0` | Filter out flights beyond this distance. Set to `0` to show everything in the bounding box |

### Polling

| Field | Default | Description |
|-------|---------|-------------|
| `IntervalSeconds` | `30` | Seconds between polls (minimum 10, enforced by the app) |

### OpenSky

| Field | Description |
|-------|-------------|
| `ClientId` | OAuth2 client ID from the OpenSky Network |
| `ClientSecret` | OAuth2 client secret |

Register at [opensky-network.org](https://opensky-network.org) to get credentials. The free tier refreshes data every 10 seconds and allows authenticated API calls.

### Telegram

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to activate notifications |
| `BotToken` | — | Token from [@BotFather](https://t.me/BotFather) |
| `ChatId` | — | Your Telegram chat ID (see below) |
| `MaxAltitudeMeters` | `3000` | Only notify for flights at or below this altitude |

**Getting your chat ID:**
1. Send any message to your bot
2. Open `https://api.telegram.org/bot{YOUR_TOKEN}/getUpdates`
3. Copy `result[0].message.chat.id`

Notifications fire when a flight is ≤ 2 minutes from its closest point to your home **and** at or below `MaxAltitudeMeters`. A photo of the aircraft (from [planespotters.net](https://www.planespotters.net)) is included when available.

## Terminal Display

The table updates every poll and shows:

| Column | Description |
|--------|-------------|
| Dist (km) | Distance from home |
| Callsign | Flight identifier |
| ICAO24 | Transponder hex code |
| Country | Origin country |
| Route | Origin → Destination (IATA codes) |
| Aircraft | Type code / Registration |
| Category | Aircraft class (Wide-body Jet, Helicopter, etc.) |
| Alt (m) | Barometric altitude |
| Speed (km/h) | Ground speed |
| Heading | Bearing in degrees + cardinal direction |
| Direction | ⊙ Overhead / ↓ Towards / ↑ Away / → Crossing |
| Overhead in | Countdown to closest approach |
| V/Rate (m/s) | Climb or descent rate |

## External APIs Used

| Service | Purpose | Auth |
|---------|---------|------|
| [OpenSky Network](https://opensky-network.org) | Live flight states | OAuth2 |
| [adsbdb.com](https://www.adsbdb.com) | Flight route (origin/destination) | None |
| [hexdb.io](https://hexdb.io) | Aircraft type, registration, operator | None |
| [planespotters.net](https://www.planespotters.net) | Aircraft photos | None |

## Project Structure

```
flightTracker/
├── Program.cs                     # Entry point, polling loop, DI setup
├── Configuration/AppSettings.cs   # Configuration schema
├── Models/                        # Data models
├── Services/                      # Business logic (interfaces + implementations)
├── Helpers/FlightDirectionHelper.cs  # Geospatial calculations (direction, ETA)
├── Display/FlightTableRenderer.cs    # Terminal UI (Spectre.Console)
└── appsettings.example.json       # Config template
```
