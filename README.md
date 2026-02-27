# Flight Tracker

A .NET 9 console app that monitors aircraft flying over your home in real time using live
ADS-B data. It displays a live terminal table and sends rich Telegram notifications when
a low-altitude aircraft is approaching overhead.

## Features

- **Live terminal table** — all flights within your configured range, updated every poll
- **Rich ADS-B data** — distance, altitude, speed, heading, climb rate, squawk, emergency status
- **Route information** — origin and destination airport names with live ETA to destination
- **Audible alert** — bell when a new flight enters the area
- **Telegram notifications** when a flight is ≤ 2 minutes from overhead and below your altitude threshold:
  - 🔁 Repeat visitor detection — "Welcome back! PH-BHO was last seen 3 days ago"
  - 🚨 Emergency and 🪖 military flagging
  - Full route: `Barcelona (BCN) → Amsterdam (AMS) · arriving in ~2h 15m`
  - Aircraft photo (from planespotters.net) and live Mapbox map with heading trajectory
  - AI-generated facts in the plane's own voice (via Anthropic Claude)
  - Wind speed/direction and outside air temperature when broadcast by the aircraft
- **Flight statistics** via `/stats` Telegram command — total sightings, busiest hours, streaks, gaps
- **Persistent SQLite history** — every notified flight is logged with full telemetry
- **EC2 deployment** — systemd service with auto-restart and helper deploy scripts
- **Graceful shutdown** — handles Ctrl+C and SIGTERM cleanly

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- (Optional) A Telegram bot token and chat ID for notifications
- (Optional) An [Anthropic API key](https://console.anthropic.com) for AI-generated aircraft facts
- (Optional) A [Mapbox access token](https://account.mapbox.com) for live map images

No flight-data API credentials are required. Flight data comes from
[airplanes.live](https://airplanes.live), a free real-time ADS-B aggregator.

## Setup

1. Copy `appsettings.example.json` to `appsettings.json`
2. Fill in your home coordinates and any optional service credentials
   (see [Configuration](#configuration))
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
  "DatabasePath": "data/flight_stats.db",
  "HomeLocation": {
    "Latitude": 51.9836,
    "Longitude": 4.6311,
    "BoundingBoxDegrees": 1.0,
    "VisualRangeKm": 50
  },
  "Polling": {
    "IntervalSeconds": 30
  },
  "AirplanesLive": {
    "BaseUrl": "https://api.airplanes.live/v2/"
  },
  "Telegram": {
    "Enabled": false,
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID",
    "MaxAltitudeMeters": 3000,
    "ErrorNotificationSnoozeMinutes": 30
  },
  "Anthropic": {
    "Enabled": false,
    "ApiKey": "YOUR_ANTHROPIC_API_KEY",
    "Model": "claude-haiku-4-5",
    "MaxTokens": 200
  },
  "Mapbox": {
    "Enabled": false,
    "AccessToken": "YOUR_MAPBOX_ACCESS_TOKEN",
    "Style": "mapbox/dark-v11"
  }
}
```

### HomeLocation

| Field | Default | Description |
|-------|---------|-------------|
| `Latitude` | — | Home latitude (WGS-84) |
| `Longitude` | — | Home longitude (WGS-84) |
| `BoundingBoxDegrees` | `1.0` | Query radius (±degrees from centre) |
| `VisualRangeKm` | `50.0` | Filter flights beyond this distance. Set to `0` to show all in the bounding box |

### Polling

| Field | Default | Description |
|-------|---------|-------------|
| `IntervalSeconds` | `30` | Seconds between polls (minimum 10, enforced by the app) |

### AirplanesLive

| Field | Default | Description |
|-------|---------|-------------|
| `BaseUrl` | `https://api.airplanes.live/v2/` | Base URL for the ADS-B API. Leave as-is unless self-hosting |

Flight data is sourced from [airplanes.live](https://airplanes.live) — no account or API
key required. The app queries the `point/{lat}/{lon}/{radius}` endpoint on every poll.

### Telegram

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to activate notifications |
| `BotToken` | — | Token from [@BotFather](https://t.me/BotFather) |
| `ChatId` | — | Your Telegram chat ID (see below) |
| `MaxAltitudeMeters` | `3000` | Only notify for flights at or below this altitude |
| `ErrorNotificationSnoozeMinutes` | `30` | Minimum minutes between repeated error alerts to prevent spam |

**Getting your chat ID:**
1. Send any message to your bot
2. Open `https://api.telegram.org/bot{YOUR_TOKEN}/getUpdates`
3. Copy `result[0].message.chat.id`

### Anthropic (AI Facts)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to append AI-generated facts to Telegram messages |
| `ApiKey` | — | Anthropic API key (get from [console.anthropic.com](https://console.anthropic.com)) |
| `Model` | `claude-haiku-4-5` | Model to use — Haiku is fast and cheap for short lookups |
| `MaxTokens` | `200` | Maximum tokens in the AI response |

When enabled, each notification ends with 2–3 sentences written from the aircraft's own
perspective — e.g. *"I'm a Boeing 787-9, registration PH-BHO, and I entered service in
2014…"*. Facts are cached per aircraft registration for the lifetime of the session so API
calls are minimal; aircraft without a known registration share one cached fact per type.

### Mapbox (Live Map)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to include a live map image in each notification |
| `AccessToken` | — | Mapbox access token (get from [account.mapbox.com](https://account.mapbox.com)) |
| `Style` | `mapbox/dark-v11` | Map style — also supports `mapbox/satellite-v9` and `mapbox/streets-v12` |

When enabled, each notification includes a static map centred on your home, showing the
aircraft's position (red pin), your home (blue pin), and the heading trajectory (orange
line). Zoom level adjusts automatically:

| Distance from home | Zoom | Approximate view width |
|--------------------|------|------------------------|
| > 13 km | 9 | ~113 km — wide regional view |
| 3–13 km | 11 | ~28 km — city-level |
| < 3 km | 13 | ~7 km — neighbourhood close-up |

The map is fetched server-side so the Mapbox token is never sent to Telegram. If the map
is unavailable the notification falls back to the aircraft photo or plain text.

## Telegram Notifications

Notifications fire when a flight is ≤ 2 minutes from its closest point to your home
**and** at or below `MaxAltitudeMeters`. A full notification looks like:

```
🔁 Welcome back! PH-BHO was last seen 3 days ago heading to LHR. This is visit #4!
🟢 KL123 — Towards | 4m 20s
London Heathrow (LHR) → Amsterdam Schiphol (AMS) · arriving in ~1h 23m
Boeing 787-9 · B789 / PH-BHO · KLM Royal Dutch Airlines
Alt: 2,450 m → FL350 | Speed: 895 km/h | 45.2 km | ETA: 4m 20s
Wind: 270° 45 kts | OAT: -52°C

✈️ I'm a Boeing 787-9, registration PH-BHO…
```

**Message sections:**

| Section | Description |
|---------|-------------|
| Repeat banner | 🔁 if seen before (with visit count + last destination), 👋 for first sighting |
| Header | Callsign, direction, ETA to overhead. 🚨 for emergencies (squawk 7700/7600/7500 or declared emergency), 🪖 for military |
| Route | Full airport names with IATA codes and live ETA to destination |
| Aircraft | Description · type code / registration · operator |
| Stats | Altitude (+ autopilot target `→ FLxxx` when set), speed, distance from home, ETA |
| Wind / temp | Wind direction + speed and outside air temperature (when broadcast by aircraft) |
| AI facts | 2–3 sentences from the aircraft's perspective (requires Anthropic enabled) |

When both a Mapbox map and a planespotters photo are available they are sent as a
two-photo album (map with the full caption, photo captionless).

## Terminal Display

The table refreshes every poll and shows all flights within `VisualRangeKm`:

| Column | Description |
|--------|-------------|
| Dist (km) | Distance from home |
| Callsign | Flight identifier |
| ICAO24 | ADS-B transponder hex code |
| Country | Origin country |
| Route | Origin → Destination (IATA codes preferred, ICAO fallback) |
| Aircraft | Type code / Registration |
| Category | Aircraft class (Wide-body Jet, Helicopter, etc.) — colour coded |
| Alt (m) | Barometric altitude |
| Speed (km/h) | Ground speed |
| Heading | Bearing in degrees + cardinal direction |
| Direction | ⊙ Overhead / ↓ Towards / ↑ Away / → Crossing |
| Overhead in | Countdown to closest approach |
| V/Rate (m/s) | Climb or descent rate |
| ETE | Estimated time en route to destination |

Flights are sorted by distance (closest first). A bell sounds when a new flight enters
the area.

## Flight Stats

Every Telegram notification is logged to a local SQLite database (`data/flight_stats.db`
by default, configurable via `DatabasePath`). Send **`stats`** or **`/stats`** to the
Telegram bot at any time for a summary:

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

The database accumulates indefinitely and is never deleted automatically. Inspect it with
[DB Browser for SQLite](https://sqlitebrowser.org) or any SQLite client.

## External APIs

| Service | Purpose | Auth |
|---------|---------|------|
| [airplanes.live](https://airplanes.live) | Real-time ADS-B flight states | None |
| [adsbdb.com](https://www.adsbdb.com) | Flight route (origin/destination airport) | None |
| [hexdb.io](https://hexdb.io) | Aircraft type code, registration, operator | None |
| [planespotters.net](https://www.planespotters.net) | Aircraft photos | None |
| [Anthropic API](https://www.anthropic.com) | AI-generated aircraft facts (optional) | API key |
| [Mapbox](https://www.mapbox.com) | Static map images (optional) | Access token |

## Deploying to EC2

The app runs as a systemd service on a Linux EC2 instance. Three scripts handle the full
lifecycle:

| Script | Where to run | When to use |
|--------|-------------|-------------|
| `ec2-setup.sh` | On EC2 | First-time setup only |
| `redeploy.sh` | On EC2 | Pull latest code and restart while SSHed in |
| `deploy.sh` | Local machine | Push an update without SSHing in |

### First-time setup (run on EC2)

```bash
# 1. Install .NET 9 SDK
# https://learn.microsoft.com/dotnet/core/install/linux

# 2. Clone the repo
git clone https://github.com/your-user/flight-tracker.git /home/ec2-user/flight-tracker

# 3. Run the setup script
cd /home/ec2-user/flight-tracker
./ec2-setup.sh
```

`ec2-setup.sh` will:
- Create `/opt/flighttracker/{app,data}` with correct ownership
- Pull the latest code and publish the app
- Prompt for all config values (coordinates, Telegram, Anthropic, Mapbox)
- Install and start the systemd service (`flighttracker.service`)

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

Both run `git pull → dotnet publish → systemctl restart` and warn if config placeholders
are still present.

### Useful EC2 commands

```bash
# Check service status
sudo systemctl status flighttracker

# Tail live logs
journalctl -u flighttracker -f

# Restart manually
sudo systemctl restart flighttracker

# Edit config in place
nano /opt/flighttracker/app/appsettings.json
sudo systemctl restart flighttracker
```

## Project Structure

```
flightTracker/
├── Program.cs                           # Entry point, polling loop, DI setup
├── Configuration/AppSettings.cs         # Configuration schema
├── Models/                              # Data models
│   ├── FlightState.cs                   # Raw ADS-B state + Haversine helper
│   ├── EnrichedFlightState.cs           # Flight + route + aircraft + photo + facts
│   ├── AircraftInfo.cs                  # Type code, registration, operator
│   ├── FlightRoute.cs                   # Origin/destination airports + coordinates
│   ├── RepeatVisitorInfo.cs             # Prior sighting count + last-seen details
│   └── FlightStats.cs                   # Aggregated stats for /stats command
├── Services/
│   ├── AirplanesLiveService.cs          # ADS-B flight data (airplanes.live)
│   ├── FlightEnrichmentService.cs       # Combines route, aircraft, photo, AI facts
│   ├── FlightRouteService.cs            # Origin/destination lookup (adsbdb.com)
│   ├── HexDbService.cs                  # Aircraft metadata (hexdb.io)
│   ├── PlaneSpottersPhotoService.cs     # Aircraft photos (planespotters.net)
│   ├── AnthropicAircraftFactsService.cs # AI facts in first-person voice (Claude)
│   ├── MapboxSnapshotService.cs         # Live map images (Mapbox Static API)
│   ├── RepeatVisitorService.cs          # Recurring aircraft detection (SQLite query)
│   ├── TelegramNotificationService.cs   # Builds and sends Telegram alerts
│   └── TelegramCommandListener.cs       # /stats command via long-polling
├── Data/SqliteFlightLoggingService.cs   # SQLite logging & stats queries
├── Helpers/FlightDirectionHelper.cs     # Geospatial math (direction, ETA, Haversine)
├── Display/FlightTableRenderer.cs       # Terminal table (Spectre.Console)
├── appsettings.example.json             # Config template (copy to appsettings.json)
├── ec2-setup.sh                         # One-time EC2 setup (run on EC2)
├── redeploy.sh                          # Pull + restart while SSHed into EC2
├── deploy.sh                            # Push updates from local machine
└── flighttracker.service                # systemd unit file
```
