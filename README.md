# Flight Tracker

A .NET 9 console app that monitors aircraft flying over your home in real time. It displays a live terminal table with flight data and sends Telegram notifications when a low-altitude aircraft is approaching overhead.

## Features

- Live terminal table showing all flights within your configured range
- Distance, route, aircraft type, altitude, speed, heading, and ETA to closest approach
- Audible alert (bell) when a new flight enters the area
- Telegram notifications (with aircraft photo) when a flight is ≤ 2 minutes from overhead and below your altitude threshold
- Optional AI-generated aircraft facts in Telegram messages (seat count, year introduced, primary uses) via Anthropic API
- Graceful shutdown on Ctrl+C or terminal close

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
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
  },
  "Anthropic": {
    "Enabled": false,
    "ApiKey": "YOUR_ANTHROPIC_API_KEY",
    "Model": "claude-haiku-4-5",
    "MaxTokens": 200
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

### Anthropic (AI Facts)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to append AI-generated facts to Telegram messages |
| `ApiKey` | — | Anthropic API key (get from [console.anthropic.com](https://console.anthropic.com)) |
| `Model` | `claude-haiku-4-5` | Model to use — Haiku is fast and cheap for short fact lookups |
| `MaxTokens` | `200` | Maximum tokens in the AI response |

When enabled, each Telegram notification includes a short paragraph of interesting facts about the aircraft type — approximate seat count, year it entered service, and primary uses. Facts are **cached per aircraft type** for the session (e.g. all Boeing 787s share one lookup), so API calls are minimal.

### Mapbox (Live Map)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to include a live map image in each Telegram notification |
| `AccessToken` | — | Mapbox access token (get from [account.mapbox.com](https://account.mapbox.com)) |
| `Style` | `mapbox/dark-v11` | Map style — also supports `mapbox/satellite-v9` and `mapbox/streets-v12` |

When enabled, each Telegram notification includes a `600×400` static map centred on your home location, showing the aircraft's position (red pin), your home (blue pin), and the flight's heading trajectory (orange line). The map is only sent when ADS-B heading data is available. Zoom level adjusts automatically based on the aircraft's distance from home:

| Distance from home | Zoom | View width |
|--------------------|------|------------|
| > 13 km | 9 | ~113 km — wide regional view |
| 3–13 km | 11 | ~28 km — city-level |
| < 3 km | 13 | ~7 km — neighbourhood close-up |

The map is fetched server-side so the Mapbox token is never exposed to Telegram. If the map request fails, the notification falls back to the aircraft photo or plain text.

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

## Flight Stats

Every time a Telegram notification fires, the sighting is logged to a SQLite database. The path defaults to `data/flight_stats.db` relative to the working directory and is configurable via `DatabasePath` in `appsettings.json`. Send **`stats`** or **`/stats`** to the Telegram bot at any time to get a summary:

```
📊 Flight Tracker Stats

✈️ Total planes tracked: 1,247
📅 Today: 43 planes (31 unique)
🏆 Busiest hour: 17:00–18:00 (avg 12.3/day)
🛫 Most spotted airline: KLM (312 sightings)
🦄 Rarest aircraft type: A388 (1 sighting)
⏱️ Longest gap: 4h 23m (12 Jan 03:17–07:40)
🔥 Current streak: 6 consecutive hours with planes
```

| Stat | Description |
|------|-------------|
| Total | All-time sighting count |
| Today | Sightings since midnight (local time) |
| Busiest hour | Hour of day (0–23) historically with the most planes |
| Most spotted airline | Operator logged the most times |
| Rarest aircraft type | ICAO type code seen the fewest times |
| Longest gap | Biggest gap between any two consecutive sightings |
| Current streak | Consecutive hours (going back from now) with at least one sighting |

The database is never deleted automatically — it accumulates over time. You can inspect it with any SQLite viewer (e.g. [DB Browser for SQLite](https://sqlitebrowser.org)).

## External APIs Used

| Service | Purpose | Auth |
|---------|---------|------|
| [OpenSky Network](https://opensky-network.org) | Live flight states | OAuth2 |
| [adsbdb.com](https://www.adsbdb.com) | Flight route (origin/destination) | None |
| [hexdb.io](https://hexdb.io) | Aircraft type, registration, operator | None |
| [planespotters.net](https://www.planespotters.net) | Aircraft photos | None |
| [Anthropic API](https://www.anthropic.com) | AI-generated aircraft facts | API key |

## Deploying to EC2

The app runs as a systemd service on a Linux EC2 instance. Three scripts handle the full lifecycle:

| Script | Where to run | When to use |
|--------|-------------|-------------|
| `ec2-setup.sh` | On EC2 | First-time setup only |
| `redeploy.sh` | On EC2 | Pull latest code and restart while SSHed in |
| `deploy.sh` | Local machine | Push an update without SSHing in |

### First-time setup (run on EC2)

```bash
# 1. Install .NET 9 SDK (https://learn.microsoft.com/dotnet/core/install/linux)

# 2. Clone the repo
git clone https://github.com/your-user/flight-tracker.git /home/ec2-user/flight-tracker

# 3. Run the setup script
cd /home/ec2-user/flight-tracker
./ec2-setup.sh
```

`ec2-setup.sh` will:
- Create `/opt/flighttracker/{app,data}` with correct ownership
- Pull the latest code and publish the app
- Prompt for all config values (coordinates, OpenSky, Telegram, Anthropic, Mapbox)
- Install and start the systemd service

```bash
# (Optional) seed historical data from your local machine
scp /path/to/flight_stats.db user@your-ec2:/opt/flighttracker/data/
```

### Subsequent updates

**From EC2** (while already SSHed in):
```bash
cd /home/ec2-user/flight-tracker && ./redeploy.sh
```

**From your local machine** (no SSH needed):
```bash
EC2_HOST=user@your-ec2-ip ./deploy.sh
```

Both do `git pull → dotnet publish → systemctl restart` and warn if config placeholders are detected.

### Useful commands on EC2

```bash
# Check service status
sudo systemctl status flighttracker

# Tail live logs
journalctl -u flighttracker -f

# Restart manually
sudo systemctl restart flighttracker

# Update config in place
nano /opt/flighttracker/app/appsettings.json
sudo systemctl restart flighttracker
```

## Project Structure

```
flightTracker/
├── Program.cs                     # Entry point, polling loop, DI setup
├── Configuration/AppSettings.cs   # Configuration schema
├── Models/                        # Data models
├── Services/                      # Business logic (interfaces + implementations)
├── Helpers/FlightDirectionHelper.cs  # Geospatial calculations (direction, ETA)
├── Display/FlightTableRenderer.cs    # Terminal UI (Spectre.Console)
├── appsettings.example.json       # Config template
├── ec2-setup.sh                   # One-time EC2 setup (run on EC2)
├── redeploy.sh                    # Pull + restart while SSHed into EC2
├── deploy.sh                      # Push updates from local machine (no SSH needed)
└── flighttracker.service          # systemd unit file
```
