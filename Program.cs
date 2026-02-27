using FlightTracker.Configuration;
using FlightTracker.Data;
using FlightTracker.Display;
using FlightTracker.Helpers;
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
services.AddSingleton<IMapSnapshotService, MapboxSnapshotService>();
services.AddSingleton<IFlightEnrichmentService, FlightEnrichmentService>();
services.AddSingleton<ITelegramNotificationService, TelegramNotificationService>();
services.AddSingleton<IFlightLoggingService, SqliteFlightLoggingService>();
services.AddSingleton<IRepeatVisitorService, RepeatVisitorService>();
services.AddSingleton<ITelegramCommandListener, TelegramCommandListener>();
await using ServiceProvider provider = services.BuildServiceProvider();

var flightService        = provider.GetRequiredService<IFlightService>();
var enrichmentService    = provider.GetRequiredService<IFlightEnrichmentService>();
var telegramService      = provider.GetRequiredService<ITelegramNotificationService>();
var loggingService       = provider.GetRequiredService<IFlightLoggingService>();
var repeatVisitorService = provider.GetRequiredService<IRepeatVisitorService>();
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

// ── Startup notification ──────────────────────────────────────────────────────
try
{
    var startupStats = await loggingService.GetStatsAsync(cts.Token);
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

// Tracks ICAO24s seen on the previous poll to detect newly-entering flights
var previousIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Tracks flights already notified via Telegram (avoids repeat messages per pass)
var notifiedIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Error notification snooze — avoids Telegram spam on repeated failures
DateTimeOffset? lastErrorNotifiedAt = null;

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        var flights  = await flightService.GetOverheadFlightsAsync(cts.Token);
        var enriched = await enrichmentService.EnrichAsync(flights, cts.Token);

        double homeLat = settings.HomeLocation.Latitude;
        double homeLon = settings.HomeLocation.Longitude;

        // Handle new flights (skipped on first poll so startup is silent)
        if (previousIcaos.Count > 0)
        {
            foreach (var ef in enriched)
            {
                if (!previousIcaos.Contains(ef.State.Icao24))
                    Console.Write('\a'); // audible alert for every new flight
            }
        }
        previousIcaos = enriched.Select(ef => ef.State.Icao24)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Telegram: notify and log when any flight is ≤ 2 minutes from overhead
        foreach (var ef in enriched)
        {
            var f = ef.State;
            double? etaSecs = FlightDirectionHelper.EtaToOverheadSeconds(
                f.Latitude, f.Longitude, f.HeadingDegrees, f.VelocityMetersPerSecond,
                homeLat, homeLon);

            if (etaSecs is <= 120.0
                && f.BarometricAltitudeMeters is not null
                && f.BarometricAltitudeMeters <= settings.Telegram.MaxAltitudeMeters
                && !notifiedIcaos.Contains(f.Icao24))
            {
                string? dir = FlightDirectionHelper.Classify(
                    f.Latitude, f.Longitude, f.HeadingDegrees, f.DistanceKm,
                    homeLat, homeLon);
                // Query BEFORE logging so the count reflects prior visits only
                var visitorInfo = await repeatVisitorService.GetVisitorInfoAsync(f.Icao24, cts.Token);
                await telegramService.NotifyAsync(ef, dir ?? "Towards", etaSecs, visitorInfo, cts.Token);
                await loggingService.LogAsync(ef, dir ?? "Towards", etaSecs, DateTimeOffset.UtcNow, cts.Token);
                notifiedIcaos.Add(f.Icao24);
            }
        }

        // Remove flights that left range so they can re-trigger if they return
        notifiedIcaos.RemoveWhere(icao => !previousIcaos.Contains(icao));

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
