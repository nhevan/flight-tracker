# Flight Tracker SSE Protocol

Real-time overhead flight notifications delivered over Server-Sent Events.

## Endpoint

```
GET /flight-tracker/events
```

### Headers

| Header | Value |
|---|---|
| `Accept` | `text/event-stream` |
| `Authorization` | `Bearer <token>` — required only when `Sse.BearerToken` is configured |

### Response headers

```
Content-Type: text/event-stream
Cache-Control: no-cache
Connection: keep-alive
X-Accel-Buffering: no
```

Returns `404` when `Sse.Enabled` is `false`. Returns `401 Unauthorized` on bad/missing token.

---

## Stream format

Standard SSE. Two message types are sent:

### Keepalive (every 25 s)

Keeps the connection alive through proxy timeouts. No payload.

```
: keepalive

```

### Flight event

Fired when a flight meets the notification criteria (ETA ≤ 120 s **and** altitude ≤ `MaxAltitudeMeters`). Also fires on re-notification when a plane changes course significantly.

```
data: <json>\n\n
```

The JSON body is a single flat object (all keys camelCase). See the schema below.

---

## Event schema

```jsonc
{
  // Identity
  "icao24": "4ca872",           // ICAO 24-bit address (hex string)
  "callsign": "KLM1234",        // ATC callsign (trimmed)

  // Position & motion
  "latitude": 51.9123,          // decimal degrees, nullable
  "longitude": 4.4789,          // decimal degrees, nullable
  "altitudeMeters": 1200.5,     // barometric altitude, nullable
  "speedKmh": 648.0,            // ground speed (m/s × 3.6), nullable
  "headingDegrees": 273.0,      // effective heading (broadcast or GPS-inferred), nullable
  "verticalRateMetersPerSecond": -4.2,  // climb(+)/descent(-), nullable

  // Proximity to the configured home location
  "distanceKm": 12.4,           // current distance, nullable
  "etaSeconds": 68.0,           // estimated time until overhead, nullable
  "direction": "Towards",       // see Direction values below

  // Route (null when route lookup is disabled or unavailable)
  "originIata": "AMS",
  "originIcao": "EHAM",
  "originName": "Amsterdam Airport Schiphol",
  "originCity": "Amsterdam",        // municipality name, nullable
  "originCountry": "Netherlands",   // nullable
  "originLat": 52.3086,             // decimal degrees, nullable
  "originLon": 4.7639,              // decimal degrees, nullable
  "destIata": "LHR",
  "destIcao": "EGLL",
  "destName": "Heathrow Airport",
  "destCity": "London",             // municipality name, nullable
  "destCountry": "United Kingdom",  // nullable
  "destLat": 51.4775,               // decimal degrees, nullable
  "destLon": -0.4614,               // decimal degrees, nullable
  "routeDistanceKm": 370.2,         // great-circle distance between airports (Haversine), nullable

  // Aircraft identity
  "aircraftDescription": "Boeing 787-9",   // human-readable model string, nullable
  "typeCode": "B789",                      // ICAO type designator, nullable
  "registration": "PH-BHO",               // tail number, nullable
  "operator": "KLM Royal Dutch Airlines", // nullable

  // Category for UI icon / animation selection
  "planeTypeCategory": "widebody-jet",    // see PlaneTypeCategory values below

  // Images
  "photoUrl": "https://cdn.planespotters.net/...",   // photo of this registration, nullable
  "silhouetteUrl": "https://www.planespotters.net/silhouettes/B789_3.png",  // nullable

  // Flags
  "isMilitary": false,
  "isEmergency": false,
  "emergency": null,      // null | "general" | "lifeguard" | "minfuel" | "nordo" | "unlawful" | "downed"
  "squawk": "2145",       // transponder squawk code, nullable
  "isCourseChange": false, // true when this is a re-notification due to a significant heading change

  // Environmental (from aircraft ACARS/ADS-B data, nullable)
  "windSpeedKnots": 22.0,
  "windDirectionDeg": 250.0,
  "outsideAirTempC": -18.5,

  // Timestamp
  "timestamp": "2026-04-21T14:32:01+00:00"  // ISO 8601, always UTC
}
```

### `direction` values

| Value | Meaning |
|---|---|
| `"Overhead"` | Plane is essentially directly above the home location |
| `"Towards"` | Plane is heading toward the home location (no precise cardinal) |
| `"N"` / `"NE"` / `"E"` / `"SE"` / `"S"` / `"SW"` / `"W"` / `"NW"` | Cardinal/intercardinal direction the plane is coming from |

### `planeTypeCategory` values

| Value | Description |
|---|---|
| `"widebody-jet"` | Twin-aisle commercial jet (e.g. 777, 787, A350) |
| `"narrowbody-jet"` | Single-aisle commercial jet (e.g. 737, A320) |
| `"turboprop"` | Turboprop aircraft |
| `"helicopter"` | Rotary wing |
| `"military"` | Military aircraft |
| `"business-jet"` | Business / executive jet |
| `"light-aircraft"` | General aviation / light aircraft |
| `"unknown"` | Could not determine category |

---

## Trigger conditions

An event fires when **all** of the following are true:

1. ETA to the configured home location is ≤ 120 seconds.
2. Barometric altitude is ≤ `Telegram.MaxAltitudeMeters` (default 3000 m).
3. The plane has not already been notified in this polling cycle — **unless** it has changed heading significantly since the last notification (`isCourseChange: true`).

The polling interval is 35 seconds, so the maximum latency between a plane entering the criteria and an event arriving is ~35 s.

---

## Connecting — minimal example

```js
const es = new EventSource('/flight-tracker/events', {
  // if auth is required:
  // fetch-based client needed for custom headers; EventSource itself doesn't support them
});

es.onmessage = (e) => {
  const flight = JSON.parse(e.data);
  console.log(flight.callsign, flight.direction, flight.etaSeconds + 's away');
};
```

With a bearer token (using a fetch-based SSE client such as `@microsoft/fetch-event-source`):

```js
fetchEventSource('/flight-tracker/events', {
  headers: { Authorization: 'Bearer <token>' },
  onmessage(e) {
    if (!e.data) return; // skip keepalives
    const flight = JSON.parse(e.data);
  },
});
```
