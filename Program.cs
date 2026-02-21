using FlightTracker.Configuration;
using FlightTracker.Display;
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
services.AddSingleton<IFlightEnrichmentService, FlightEnrichmentService>();
await using ServiceProvider provider = services.BuildServiceProvider();

var flightService     = provider.GetRequiredService<IFlightService>();
var enrichmentService = provider.GetRequiredService<IFlightEnrichmentService>();

// ── Graceful shutdown (Ctrl+C) ────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // prevent abrupt process termination
    cts.Cancel();
    Console.WriteLine();
    Console.WriteLine("Shutting down gracefully...");
};

// ── Polling Loop ─────────────────────────────────────────────────────────────
Console.WriteLine("Flight Tracker starting. Press Ctrl+C to exit.");
Console.WriteLine();

TimeSpan pollInterval = TimeSpan.FromSeconds(settings.Polling.IntervalSeconds);

while (!cts.Token.IsCancellationRequested)
{
    try
    {
        var flights  = await flightService.GetOverheadFlightsAsync(cts.Token);
        var enriched = await enrichmentService.EnrichAsync(flights, cts.Token);

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
