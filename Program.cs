using FlightTracker.Configuration;
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
services.AddSingleton<IOpenSkyTokenProvider, OpenSkyTokenProvider>();
services.AddSingleton<IFlightService, OpenSkyService>();
services.AddSingleton<IFlightRouteService, FlightRouteService>();
services.AddSingleton<IAircraftInfoService, HexDbService>();
services.AddSingleton<IAircraftPhotoService, PlaneSpottersPhotoService>();
services.AddSingleton<IAircraftFactsService, AnthropicAircraftFactsService>();
services.AddSingleton<IFlightEnrichmentService, FlightEnrichmentService>();
services.AddSingleton<ITelegramNotificationService, TelegramNotificationService>();
await using ServiceProvider provider = services.BuildServiceProvider();

var flightService     = provider.GetRequiredService<IFlightService>();
var enrichmentService = provider.GetRequiredService<IFlightEnrichmentService>();
var telegramService   = provider.GetRequiredService<ITelegramNotificationService>();

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
// or the OS / Docker / systemd asks the process to stop
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!cts.IsCancellationRequested)
        cts.Cancel();
};

// ── Polling Loop ─────────────────────────────────────────────────────────────
Console.WriteLine("Flight Tracker starting. Press Ctrl+C to exit.");
Console.WriteLine();

TimeSpan pollInterval = TimeSpan.FromSeconds(settings.Polling.IntervalSeconds);

// Tracks ICAO24s seen on the previous poll to detect newly-entering flights
var previousIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Tracks flights already notified via Telegram (avoids repeat messages per pass)
var notifiedIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        // Telegram: notify when any flight is ≤ 2 minutes from overhead
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
                await telegramService.NotifyAsync(ef, dir ?? "Towards", etaSecs, cts.Token);
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
    catch (OperationCanceledException)
    {
        break;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[HTTP Error] {ex.StatusCode}: {ex.Message}");
        Console.WriteLine("Retrying on next poll...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("Retrying on next poll...");
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
