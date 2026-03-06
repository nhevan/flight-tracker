using FlightTracker.Configuration;
using FlightTracker.Data;
using FlightTracker.Display;
using FlightTracker.Helpers;
using FlightTracker.Models;
using FlightTracker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── Configuration ────────────────────────────────────────────────────────────
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

var settings = new AppSettings();
configuration.Bind(settings);

// Enforce minimum polling interval — OpenSky states refresh every 10 seconds
const int MinimumIntervalSeconds = 10;
if (settings.Polling.IntervalSeconds < MinimumIntervalSeconds)
{
    Console.WriteLine(
        $"Warning: IntervalSeconds clamped to minimum {MinimumIntervalSeconds}s " +
        $"to respect OpenSky rate limits.");
    settings.Polling.IntervalSeconds = MinimumIntervalSeconds;
}

// ── DI Container ─────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddSingleton(settings);
// IHttpClientFactory manages connection pools for all HTTP clients in the app
services.AddHttpClient();
services.AddSingleton<IFlightService, AirplanesLiveService>();
services.AddSingleton<IFlightRouteService, FlightRouteService>();
services.AddSingleton<IAircraftInfoService, HexDbService>();
services.AddSingleton<IAircraftPhotoService, PlaneSpottersPhotoService>();
services.AddSingleton<IAircraftFactsService, AnthropicAircraftFactsService>();
services.AddSingleton<IAnthropicChatService, AnthropicChatService>();
services.AddSingleton<IMapSnapshotService, MapboxSnapshotService>();
services.AddSingleton<IArinc424NavDataService>(_ =>
    new Arinc424NavDataService(Path.Combine(AppContext.BaseDirectory, "flightLegDataArinc", "arinc_eh")));
services.AddSingleton<INavigraphNavDataService>(_ =>
    new NavigraphNavDataService(Path.Combine(AppContext.BaseDirectory, "flightLegDataArinc", "little_navmap_navigraph.sqlite")));
services.AddHttpClient("flightplandatabase", c =>
{
    c.BaseAddress = new Uri("https://api.flightplandatabase.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
});
services.AddSingleton<IFlightPlanDBService, FlightPlanDBService>();
services.AddSingleton<IPredictedPathService, PredictedPathService>();
services.AddSingleton<IFlightEnrichmentService, FlightEnrichmentService>();
services.AddSingleton<ITelegramNotificationService, TelegramNotificationService>();
services.AddSingleton<IFlightLoggingService, SqliteFlightLoggingService>();
services.AddSingleton<IRepeatVisitorService, RepeatVisitorService>();
services.AddSingleton<IFlightTrajectoryService, SqliteFlightTrajectoryService>();
services.AddSingleton<ITelegramCommandListener, TelegramCommandListener>();
await using ServiceProvider provider = services.BuildServiceProvider();

var flightService        = provider.GetRequiredService<IFlightService>();
var enrichmentService    = provider.GetRequiredService<IFlightEnrichmentService>();
var telegramService      = provider.GetRequiredService<ITelegramNotificationService>();
var loggingService       = provider.GetRequiredService<IFlightLoggingService>();
var repeatVisitorService = provider.GetRequiredService<IRepeatVisitorService>();
var trajectoryService    = provider.GetRequiredService<IFlightTrajectoryService>();
var commandListener      = provider.GetRequiredService<ITelegramCommandListener>();

// ── Graceful shutdown (Ctrl+C and terminal close) ────────────────────────────
using var cts = new CancellationTokenSource();

// Ctrl+C in an interactive terminal
// Note: do NOT set e.Cancel = true — that would suppress the signal on the
// parent "dotnet run" process, causing it to exit and orphan this child process.
// Instead we cancel our token and let the process exit naturally.
Console.CancelKeyPress += (_, e) =>
{
    cts.Cancel();
    Console.WriteLine();
    Console.WriteLine("Shutting down gracefully...");
};

// SIGTERM — sent when the terminal window is closed, the parent process dies,
// or the OS / Docker / systemd asks the process to stop.
// On Linux, ProcessExit fires twice: once on SIGTERM, and again when the CLR
// shuts down after Main returns. By the second firing, cts is already disposed,
// so we guard against ObjectDisposedException to prevent an ABRT crash-loop.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        if (!cts.IsCancellationRequested)
            cts.Cancel();
    }
    catch (ObjectDisposedException) { }
};

// ── Initialise database ───────────────────────────────────────────────────────
await loggingService.InitialiseAsync(cts.Token);
await trajectoryService.InitialiseAsync(cts.Token);

// ── Startup notification ──────────────────────────────────────────────────────
try
{
    var startupStats = await loggingService.GetStatsAsync(settings.HomeLocation.Latitude, settings.HomeLocation.Longitude, cts.Token);
    await telegramService.SendStatusAsync(FormatStartupMessage(startupStats), cts.Token);
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] Stats unavailable: {ex.Message}");
    await telegramService.SendStatusAsync("🟢 <b>FlightTracker started</b>\n⚠️ DB stats unavailable at startup.", cts.Token);
}

// ── Start Telegram command listener (background) ──────────────────────────────
_ = commandListener.StartAsync(cts.Token);

// ── Polling Loop ─────────────────────────────────────────────────────────────
Console.WriteLine("Flight Tracker starting. Press Ctrl+C to exit.");
Console.WriteLine();

TimeSpan pollInterval = TimeSpan.FromSeconds(settings.Polling.IntervalSeconds);

// Tracks the last known GPS position per ICAO24 so we can infer heading when
// an aircraft doesn't broadcast HeadingDegrees (position delta → initial bearing).
var previousPositions = new Dictionary<string, (double Lat, double Lon)>(
    StringComparer.OrdinalIgnoreCase);

// Accumulates all GPS positions per ICAO24 during the current continuous visit.
// Used to draw the full trajectory polyline on course-change map notifications.
// Entries are removed when the aircraft leaves range, so re-appearing planes
// start fresh — only the current visit is stored, never historical DB data.
var positionHistory = new Dictionary<string, List<(double Lat, double Lon)>>(
    StringComparer.OrdinalIgnoreCase);

// Tracks flights already notified via Telegram.
// Maps ICAO24 → effective heading at the time the notification was sent so a
// significant bearing change (≥ 45°) can trigger a fresh notification even when
// the aircraft is still in range.
var notifiedIcaos = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

// Error notification snooze — avoids Telegram spam on repeated failures
DateTimeOffset? lastErrorNotifiedAt = null;

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        // Apply a /spot command that arrived since the last poll
        if (settings.HomeLocation.LocationResetRequested)
        {
            settings.HomeLocation.LocationResetRequested = false;
            previousPositions.Clear();
            notifiedIcaos.Clear();
            positionHistory.Clear();
            string locLabel = settings.HomeLocation.Name is { } n ? $"\"{n}\" " : "";
            Console.WriteLine($"[Spot] Location changed to {locLabel}" +
                              $"({settings.HomeLocation.Latitude:F6}, {settings.HomeLocation.Longitude:F6})" +
                              " — state reset.");
        }

        var flights     = await flightService.GetOverheadFlightsAsync(cts.Token);
        var rawEnriched = await enrichmentService.EnrichAsync(flights, cts.Token);

        // Inject inferred heading for aircraft that don't broadcast HeadingDegrees.
        // Computed from the GPS position delta relative to the previous poll.
        // First-poll aircraft (no prior position) keep InferredHeadingDegrees = null.
        IReadOnlyList<EnrichedFlightState> enriched = rawEnriched
            .Select(ef =>
            {
                var f = ef.State;
                if (f.HeadingDegrees is not null
                    || f.Latitude is null || f.Longitude is null
                    || !previousPositions.TryGetValue(f.Icao24, out var prev))
                    return ef;
                double? inferred = FlightDirectionHelper.InferHeading(
                    prev.Lat, prev.Lon, f.Latitude.Value, f.Longitude.Value);
                return inferred is null ? ef : ef with { InferredHeadingDegrees = inferred };
            })
            .ToList();

        double homeLat = settings.HomeLocation.Latitude;
        double homeLon = settings.HomeLocation.Longitude;

        // Handle new flights (skipped on first poll so startup is silent)
        if (previousPositions.Count > 0)
        {
            foreach (var ef in enriched)
            {
                if (!previousPositions.ContainsKey(ef.State.Icao24))
                    Console.Write('\a'); // audible alert for every new flight
            }
        }

        // Update previous positions for the next poll's heading inference
        previousPositions.Clear();
        foreach (var ef in enriched)
        {
            var fs = ef.State;
            if (fs.Latitude.HasValue && fs.Longitude.HasValue)
                previousPositions[fs.Icao24] = (fs.Latitude.Value, fs.Longitude.Value);
        }

        // Maintain per-aircraft trajectory history (current visit only).
        // Append new position, cap at 60 entries (~30 min at 30 s polling),
        // and remove entries for aircraft that have left range.
        var currentIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ef in enriched)
        {
            var fs = ef.State;
            if (!fs.Latitude.HasValue || !fs.Longitude.HasValue) continue;
            currentIcaos.Add(fs.Icao24);
            if (!positionHistory.TryGetValue(fs.Icao24, out var hist))
                positionHistory[fs.Icao24] = hist = new List<(double, double)>();
            hist.Add((fs.Latitude.Value, fs.Longitude.Value));
            if (hist.Count > 60) hist.RemoveAt(0);
        }
        foreach (var key in positionHistory.Keys.Except(currentIcaos).ToList())
            positionHistory.Remove(key);

        // ── Trajectory recording: track Rotterdam arrivals and departures ─────
        // Record a position point for each Rotterdam flight every poll cycle.
        // Sessions are opened on first sight and closed when the flight leaves
        // the visual range set via /range (AirplanesLiveService already filters
        // those out, so they simply stop appearing in poll results).
        var airport = settings.TrackedAirport;
        foreach (var ef in enriched)
        {
            var route = ef.Route;
            if (route is null) continue;

            bool isArriving  = string.Equals(route.DestIcao,   airport.IcaoCode, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(route.DestIata,   airport.IataCode, StringComparison.OrdinalIgnoreCase);
            bool isDeparting = string.Equals(route.OriginIcao, airport.IcaoCode, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(route.OriginIata, airport.IataCode, StringComparison.OrdinalIgnoreCase);

            if (!isArriving && !isDeparting) continue;

            string flightType = isArriving ? "Arriving" : "Departing";
            await trajectoryService.StartOrContinueAsync(ef, flightType, cts.Token);
        }

        // Close sessions for flights that have left the visual range
        foreach (var icao in trajectoryService.GetActiveIcaos()
            .Where(icao => !currentIcaos.Contains(icao)).ToList())
        {
            await trajectoryService.CompleteSessionAsync(icao, cts.Token);
        }


        // Telegram: notify and log when any flight is ≤ 2 minutes from overhead
        foreach (var ef in enriched)
        {
            var f = ef.State;
            double? effectiveHeading = f.HeadingDegrees ?? ef.InferredHeadingDegrees;
            double? etaSecs = FlightDirectionHelper.EtaToOverheadSeconds(
                f.Latitude, f.Longitude, effectiveHeading, f.VelocityMetersPerSecond,
                homeLat, homeLon);

            bool alreadyNotified = notifiedIcaos.TryGetValue(f.Icao24, out double? lastHeading);
            bool bearingChanged  = FlightDirectionHelper.HeadingChangedSignificantly(lastHeading, effectiveHeading);

            if (etaSecs is <= 120.0
                && f.BarometricAltitudeMeters is not null
                && f.BarometricAltitudeMeters <= settings.Telegram.MaxAltitudeMeters
                && (!alreadyNotified || bearingChanged))
            {
                string? dir = FlightDirectionHelper.Classify(
                    f.Latitude, f.Longitude, effectiveHeading, f.DistanceKm,
                    homeLat, homeLon);
                // Query repeat-visitor info only for initial notifications;
                // course-change re-notifications are part of the same overflight, not a new visit.
                var visitorInfo = bearingChanged
                    ? null
                    : await repeatVisitorService.GetVisitorInfoAsync(f.Icao24, cts.Token);
                IReadOnlyList<(double Lat, double Lon)>? trajectory = bearingChanged
                    && positionHistory.TryGetValue(f.Icao24, out var hist)
                    ? hist
                    : null;

                await telegramService.NotifyAsync(ef, dir ?? "Towards", etaSecs, visitorInfo, cts.Token,
                    homeLat, homeLon,
                    previousHeading: bearingChanged ? lastHeading : null,
                    trajectory: trajectory,
                    isBeingRecorded: trajectoryService.IsTracking(f.Icao24));
                // Only log as a new visit for initial notifications — course-change re-notifications
                // are part of the same overflight and must not inflate the visit counter.
                if (!bearingChanged)
                    await loggingService.LogAsync(ef, dir ?? "Towards", etaSecs, homeLat, homeLon, settings.HomeLocation.Name, DateTimeOffset.UtcNow, cts.Token);
                notifiedIcaos[f.Icao24] = effectiveHeading;
            }
        }

        // Remove flights that left range so they can re-trigger if they return
        foreach (var icao in notifiedIcaos.Keys
            .Where(icao => !previousPositions.ContainsKey(icao)).ToList())
            notifiedIcaos.Remove(icao);

        // Skip the interactive table when stdout is redirected (e.g. systemd journal)
        // — box-drawing characters produce garbled multi-line journal entries.
        if (!Console.IsOutputRedirected)
            FlightTableRenderer.Render(
                enriched,
                settings.HomeLocation.Latitude,
                settings.HomeLocation.Longitude,
                settings.HomeLocation.VisualRangeKm,
                DateTimeOffset.Now);
    }
    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
    {
        break;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[HTTP Error] {ex.StatusCode}: {ex.Message}");
        Console.WriteLine("Retrying on next poll...");
        await NotifyErrorAsync($"⚠️ <b>FlightTracker HTTP error</b>\n{ex.StatusCode}: {EscapeHtml(ex.Message)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("Retrying on next poll...");
        await NotifyErrorAsync($"⚠️ <b>FlightTracker error</b>\n{ex.GetType().Name}: {EscapeHtml(ex.Message)}");
    }

    try
    {
        await Task.Delay(pollInterval, cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

Console.WriteLine("Flight Tracker stopped.");

// ── Local helpers ─────────────────────────────────────────────────────────────

async Task NotifyErrorAsync(string htmlMessage)
{
    var now = DateTimeOffset.UtcNow;
    int snoozeMins = settings.Telegram.ErrorNotificationSnoozeMinutes;
    if (lastErrorNotifiedAt is not null &&
        (now - lastErrorNotifiedAt.Value).TotalMinutes < snoozeMins)
        return;

    lastErrorNotifiedAt = now;
    await telegramService.SendStatusAsync(htmlMessage, cts.Token);
}

static string FormatStartupMessage(FlightTracker.Models.FlightStats s)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("🟢 <b>FlightTracker started</b>");
    sb.AppendLine();
    sb.AppendLine("📊 <b>Database summary</b>");
    sb.AppendLine($"• Total sightings: {s.TotalSightings:N0}");
    sb.AppendLine($"• Today: {s.TodayCount} flights, {s.TodayUniqueAircraft} unique aircraft");

    if (s.CurrentStreakHours > 0)
        sb.AppendLine($"• Current streak: {s.CurrentStreakHours} h");

    if (s.BusiestHour.HasValue)
        sb.AppendLine($"• Busiest hour: {s.BusiestHour:D2}:00 (avg {s.BusiestHourAvgPerDay:F1}/day)");

    if (!string.IsNullOrEmpty(s.MostSpottedAirline))
        sb.AppendLine($"• Most spotted: {s.MostSpottedAirline} ({s.MostSpottedAirlineCount}×)");

    if (s.LongestGap.HasValue)
    {
        var gap = s.LongestGap.Value;
        string gapStr = gap.TotalHours >= 1
            ? $"{(int)gap.TotalHours} h {gap.Minutes} m"
            : $"{gap.Minutes} m";
        sb.AppendLine($"• Longest sky gap: {gapStr}");
    }

    return sb.ToString().TrimEnd();
}

static string EscapeHtml(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
