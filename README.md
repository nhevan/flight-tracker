# Flight Tracker

A .NET 9 console app that monitors aircraft flying over your home in real time using live
ADS-B data. It displays a live terminal table and sends rich Telegram notifications when
a low-altitude aircraft is approaching overhead.

## Features

- **Live terminal table** — all flights within your configured range, updated every poll
- **Rich ADS-B data** — distance, altitude, speed, heading, climb rate, squawk, emergency status
- **Route information** — origin and destination airport names with live ETA to destination
- **Predicted flight path** — blue route overlay on the map, inferred entirely offline from the [Navigraph / Little NavMap SQLite database](https://littlenavmap.org) by snapping the aircraft's current position and heading to the nearest matching airway and chaining up to 8 consecutive airways toward the destination
- **Audible alert** — bell when a new flight enters the area
- **Telegram notifications** when a flight is ≤ 2 minutes from overhead and below your altitude threshold:
  - 🔁 Repeat visitor detection — "Welcome back! PH-BHO was last seen 3 days ago"
  - 🚨 Emergency and 🪖 military flagging
  - ↩️ Course-change re-notification when a tracked flight changes bearing significantly, with full accumulated trajectory polyline overlay on the map
  - Full route: `Barcelona (BCN) → Amsterdam (AMS) · arriving in ~2h 15m`
  - Aircraft photo (from planespotters.net) and live Mapbox map with heading trajectory
  - AI-generated facts in the plane's own voice (via Anthropic Claude) — omitted on course-change re-notifications
  - Wind speed/direction and outside air temperature when broadcast by the aircraft
  - Tappable callsign link that opens FlightRadar24 centred on your spot (zoom 11)
- **Telegram bot commands** — manage spots, adjust range and altitude filter, control map zoom, query stats, send test notifications, and plot any live flight on demand with `/plot <callsign>`
- **Smart fallback replies** — unknown messages are forwarded to Claude for a helpful plain-text response
- **Flight statistics** via `/stats` — total sightings, busiest hours, streaks, gaps
- **Named spot management** — save and switch between multiple named spotting locations
- **Persistent SQLite history** — every notified flight is logged with full telemetry
- **EC2 deployment** — systemd service with auto-restart and helper deploy scripts
- **Graceful shutdown** — handles Ctrl+C and SIGTERM cleanly

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- (Optional) A Telegram bot token and chat ID for notifications
- (Optional) An [Anthropic API key](https://console.anthropic.com) for AI-generated aircraft facts and smart bot replies
- (Optional) A [Mapbox access token](https://account.mapbox.com) for live map images
- (Optional) A [Navigraph / Little NavMap SQLite database](https://littlenavmap.org) (`little_navmap_navigraph.sqlite`) for predicted airway path overlay — place at `flightLegDataArinc/little_navmap_navigraph.sqlite`

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
    "Name": "Home",
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
    "Style": "mapbox/dark-v11",
    "ZoomOverride": null,
    "BearingOverride": null
  }
}
```

### HomeLocation

| Field | Default | Description |
|-------|---------|-------------|
| `Latitude` | — | Home latitude (WGS-84) |
| `Longitude` | — | Home longitude (WGS-84) |
| `Name` | — | Optional human-readable name for this spot (e.g. `"Home"`, `"Roof terrace"`) |
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

### Anthropic (AI Facts + Smart Replies)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to enable both AI aircraft facts and smart bot replies |
| `ApiKey` | — | Anthropic API key (get from [console.anthropic.com](https://console.anthropic.com)) |
| `Model` | `claude-haiku-4-5` | Model to use — Haiku is fast and cheap for short lookups |
| `MaxTokens` | `200` | Maximum tokens in the AI response |

When enabled:
- Each notification ends with 2–3 sentences written from the aircraft's own perspective. Facts are cached per aircraft registration for the session lifetime so API calls are minimal.
- Any unrecognised bot message is forwarded to Claude, which replies helpfully in plain text (e.g. answering aviation questions or guiding you to valid commands).
- AI facts are **not** appended to course-change re-notifications.

### Mapbox (Live Map)

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Set to `true` to include a live map image in each notification |
| `AccessToken` | — | Mapbox access token (get from [account.mapbox.com](https://account.mapbox.com)) |
| `Style` | `mapbox/dark-v11` | Map style — also supports `mapbox/satellite-v9` and `mapbox/streets-v12` |
| `ZoomOverride` | `null` | Fixed zoom level (1–22). When set, all maps use this zoom instead of the automatic distance-based selection. Updated at runtime by `/zoom` |
| `BearingOverride` | `null` | Map bearing in degrees clockwise from north (0–359). `null` uses the default 350° (10° anti-clockwise). Updated at runtime by `/rotate` |

When enabled, each notification includes a static map centred on your home, showing the
aircraft's position (red pin), your home (blue pin), and the heading trajectory (orange
line with direction arrow). On course-change re-notifications the full accumulated GPS
track is drawn as the trajectory polyline.

When the Navigraph database is present, a **blue predicted path** is overlaid on the map
showing the inferred route ahead of the aircraft across chained airways toward the destination.

### Navigraph / Little NavMap SQLite (Predicted Flight Path)

The predicted path is computed entirely offline — no external API calls, no API key required.

Place the `little_navmap_navigraph.sqlite` file (from [Little NavMap](https://littlenavmap.org)
or a [Navigraph](https://navigraph.com) export) at:

```
flightLegDataArinc/little_navmap_navigraph.sqlite
```

> **Note:** This file is ~134 MB and is not included in the repository. It must be copied
> manually to the EC2 instance at `/opt/flighttracker/app/flightLegDataArinc/little_navmap_navigraph.sqlite`.

When the file is present, each notification includes:

- **Airway snapping** — the aircraft's position and heading are matched to the nearest
  heading-aligned airway segment in the `airway` table (86 000+ worldwide segments).
- **Multi-airway chaining** — after each airway ends, the algorithm finds the next
  aligned airway from the last waypoint and chains up to 8 consecutive airways toward
  the destination. A typical 1 000 km route produces 25–30 waypoints.
- **Fallback** — when no matching airway is found (e.g. oceanic track, unpublished route),
  a direct great-circle line from origin to destination is used instead.
- **Nav Data log** in the notification — shows the full chain, e.g.
  `UY131 → Z319 → UL194 → UN860 · 29 wpts (312 segs scanned)`.

**ARINC 424 data note:** The bundled `flightLegDataArinc/arinc_eh/` file is an
open-flightmaps airspace-boundary dataset for the EH (Netherlands) region. It contains
only 4 named enroute waypoints and ~40 terminal-area fixes — not IFR airways. It is not
used for predicted path computation; only the Navigraph SQLite database is.

Zoom level is selected automatically based on the plane's distance from home:

| Distance from home | Zoom | Approximate view width |
|--------------------|------|------------------------|
| > 13 km | 10 | ~56 km — wide regional view |
| 3–13 km | 12 | ~14 km — city-level |
| < 3 km | 14 | ~3.5 km — neighbourhood close-up |

Use `/zoom <level>` to override the automatic selection; `/zoom auto` reverts to the
distance-based logic. The current override is persisted to `appsettings.json`.

The map is fetched server-side so the Mapbox token is never sent to Telegram. If the map
is unavailable the notification falls back to the aircraft photo or plain text.

## Telegram Bot Commands

Register these with [@BotFather](https://t.me/BotFather) via `/setcommands`:

```
stats - Show flight statistics for the current spot
spot - Set spotting location by coordinates or name
spots - List all known spot names
range - Set visual range filter in km
zoom - Set map zoom level (1-22) or auto
alt - Set max altitude filter in metres
rotate - Set map bearing in degrees (0-359) or reset
test - Send a test flight notification
plot - Plot a live flight by callsign (e.g. HV6992)
```

### Command reference

| Command | Description |
|---------|-------------|
| `/stats` | Aggregated flight statistics for the current spot |
| `/spot <lat> <lon> [name]` | Set the active spotting location by coordinates. The optional name is saved and shown in notifications |
| `/spot <name>` | Switch to a previously named spot by looking up its most recent coordinates from the flight history database |
| `/spots` | List all named spots recorded in the flight history database, plus the current active spot if named |
| `/range <km>` | Update `VisualRangeKm` at runtime (e.g. `/range 30`). Use `/range 0` to disable filtering. Persisted to `appsettings.json` |
| `/zoom <1–22>` | Pin the Mapbox map to a fixed zoom level (e.g. `/zoom 12`). Persisted to `appsettings.json` |
| `/zoom auto` | Revert to automatic distance-based zoom selection |
| `/alt <100–15000>` | Set the max altitude filter in metres (e.g. `/alt 5000`). Persisted to `appsettings.json` |
| `/alt` | Show the current max altitude setting |
| `/rotate <0–359>` | Set the map bearing in degrees clockwise from north (e.g. `/rotate 0` = north up, `/rotate 90` = east up). Persisted to `appsettings.json` |
| `/rotate reset` | Revert to the default bearing (350° — 10° anti-clockwise) |
| `/rotate` | Show the current bearing |
| `/test` | Send a synthetic notification through the full pipeline to verify everything is wired up correctly |
| `/plot <callsign>` | Look up a flight live, compute its predicted airway path, and send you the full notification with map (e.g. `/plot HV6992`) |

Any other text is forwarded to Claude (when Anthropic is enabled), which replies helpfully in plain text.

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
🔵 Route: UY131 → Z319 → UL194 · 22 waypoints

📋 Nav Data
  Navigraph ✓ UY131 → Z319 → UL194 · 22 wpts (298 segs scanned)
  ARINC: terminal area only (4 NL fixes, not applicable)

✈️ I'm a Boeing 787-9, registration PH-BHO…
```

**Message sections:**

| Section | Description |
|---------|-------------|
| Repeat banner | 🔁 if seen before (with visit count + last destination), 👋 for first sighting |
| Header | Callsign (tappable — opens FlightRadar24 at your spot), direction, ETA to overhead. 🚨 for emergencies (squawk 7700/7600/7500 or declared emergency), 🪖 for military |
| Route | Full airport names with IATA codes and live ETA to destination |
| Aircraft | Description · type code / registration · operator |
| Stats | Altitude (+ autopilot target `→ FLxxx` when set), speed, distance from home, ETA |
| Wind / temp | Wind direction + speed and outside air temperature (when broadcast by aircraft) |
| Route status | 🔵 with airway chain + waypoint count, or ⚪ when unavailable |
| Nav Data | Airway chain used (e.g. `UY131 → Z319 → UN860`), waypoints, segments scanned, and ARINC status |
| AI facts | 2–3 sentences from the aircraft's perspective (requires Anthropic; omitted on course-change re-notifications) |

The callsign in the header is a deep link (`http://fr24.com/<lat>,<lon>/11`) that opens
FlightRadar24 centred on your spotting location at zoom level 11.

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
by default, configurable via `DatabasePath`). Send **`/stats`** to the bot at any time
for a summary:

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

The database accumulates indefinitely and is never deleted automatically. Inspect it with
[DB Browser for SQLite](https://sqlitebrowser.org) or any SQLite client.

## External APIs

| Service | Purpose | Auth |
|---------|---------|------|
| [airplanes.live](https://airplanes.live) | Real-time ADS-B flight states | None |
| [adsbdb.com](https://www.adsbdb.com) | Flight route (origin/destination airport) | None |
| [hexdb.io](https://hexdb.io) | Aircraft type code, registration, operator | None |
| [planespotters.net](https://www.planespotters.net) | Aircraft photos | None |
| [Anthropic API](https://www.anthropic.com) | AI aircraft facts + smart bot replies (optional) | API key |
| [Mapbox](https://www.mapbox.com) | Static map images (optional) | Access token |
| Navigraph / Little NavMap SQLite | Airway segments for predicted path overlay (optional, ~134 MB file) | None (file on disk) |

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
├── Program.cs                             # Entry point, polling loop, DI setup
├── Configuration/AppSettings.cs           # Configuration schema
├── Models/                                # Data models
│   ├── FlightState.cs                     # Raw ADS-B state + Haversine helper
│   ├── EnrichedFlightState.cs             # Flight + route + aircraft + photo + facts + predicted path
│   ├── AircraftInfo.cs                    # Type code, registration, operator
│   ├── FlightRoute.cs                     # Origin/destination airports + coordinates
│   ├── NavFix.cs                          # Navigational fix (name, lat, lon, type)
│   ├── PredictedFlightPath.cs             # Ordered lat/lon coordinates of the predicted ahead-path
│   ├── RepeatVisitorInfo.cs               # Prior sighting count + last-seen details
│   └── FlightStats.cs                     # Aggregated stats for /stats command
├── Services/
│   ├── AirplanesLiveService.cs            # ADS-B flight data (airplanes.live); includes callsign lookup
│   ├── FlightEnrichmentService.cs         # Combines route, aircraft, photo, AI facts, predicted path
│   ├── FlightRouteService.cs              # Origin/destination lookup (adsbdb.com)
│   ├── NavigraphNavDataService.cs         # Airway snapping + multi-chain path inference (Navigraph SQLite)
│   ├── Arinc424NavDataService.cs          # ARINC 424 navdata parser (terminal-area fix resolution)
│   ├── PredictedPathService.cs            # Airway-chained path builder with direct fallback
│   ├── HexDbService.cs                    # Aircraft metadata (hexdb.io)
│   ├── PlaneSpottersPhotoService.cs       # Aircraft photos (planespotters.net)
│   ├── AnthropicAircraftFactsService.cs   # AI facts in first-person voice (Claude)
│   ├── AnthropicChatService.cs            # Claude replies for unknown bot messages
│   ├── MapboxSnapshotService.cs           # Live map images (Mapbox Static API)
│   ├── RepeatVisitorService.cs            # Recurring aircraft detection (SQLite)
│   ├── TelegramNotificationService.cs     # Builds and sends Telegram flight alerts
│   └── TelegramCommandListener.cs         # Bot command handler (long-polling; /plot + others)
├── Data/
│   ├── IFlightLoggingService.cs           # Logging + stats + spot query interface
│   └── SqliteFlightLoggingService.cs      # SQLite implementation
├── Helpers/
│   ├── FlightDirectionHelper.cs           # Geospatial math (direction, ETA, Haversine)
│   └── FlyByArcHelper.cs                  # Fly-by turn arc geometry (R=TAS²/g·tan25°, arc sampling)
├── Display/FlightTableRenderer.cs         # Terminal table (Spectre.Console)
├── flightLegDataArinc/
│   ├── arinc_eh/                          # open-flightmaps ARINC 424 (EH region, terminal area only)
│   │   ├── embedded/arinc_eh.pc           # Full European fixes + navaids (~83K records)
│   │   └── isolated/arinc_eh.pc           # EH-region-only subset (~16K records)
│   └── little_navmap_navigraph.sqlite     # Navigraph airway database (~134 MB, NOT in git — copy manually)
├── appsettings.example.json               # Config template (copy to appsettings.json)
├── ec2-setup.sh                           # One-time EC2 setup (run on EC2)
├── redeploy.sh                            # Pull + restart while SSHed into EC2
├── deploy.sh                              # Push updates from local machine
└── flighttracker.service                  # systemd unit file
```
