using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Data;
using FlightTracker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── Host setup ────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

var settings = new AppSettings();
builder.Configuration.Bind(settings);

// Enforce minimum polling interval
const int MinimumIntervalSeconds = 10;
if (settings.Polling.IntervalSeconds < MinimumIntervalSeconds)
{
    Console.WriteLine(
        $"Warning: IntervalSeconds clamped to minimum {MinimumIntervalSeconds}s " +
        $"to respect OpenSky rate limits.");
    settings.Polling.IntervalSeconds = MinimumIntervalSeconds;
}

// ── DI Container ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IFlightService, AirplanesLiveService>();
builder.Services.AddSingleton<IFlightRouteService, FlightRouteService>();
builder.Services.AddSingleton<IAircraftInfoService, HexDbService>();
builder.Services.AddSingleton<IAircraftPhotoService, PlaneSpottersPhotoService>();
builder.Services.AddSingleton<IAircraftFactsService, AnthropicAircraftFactsService>();
builder.Services.AddSingleton<IAnthropicChatService, AnthropicChatService>();
builder.Services.AddSingleton<IMapSnapshotService, MapboxSnapshotService>();
builder.Services.AddSingleton<IArinc424NavDataService>(_ =>
    new Arinc424NavDataService(Path.Combine(AppContext.BaseDirectory, "flightLegDataArinc", "arinc_eh")));
builder.Services.AddSingleton<INavigraphNavDataService>(_ =>
    new NavigraphNavDataService(Path.Combine(AppContext.BaseDirectory, "flightLegDataArinc", "little_navmap_navigraph.sqlite")));
builder.Services.AddHttpClient("flightplandatabase", c =>
{
    c.BaseAddress = new Uri("https://api.flightplandatabase.com/");
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FlightTracker/1.0");
});
builder.Services.AddSingleton<IFlightPlanDBService, FlightPlanDBService>();
builder.Services.AddSingleton<IPredictedPathService, PredictedPathService>();
builder.Services.AddSingleton<IFlightEnrichmentService, FlightEnrichmentService>();
builder.Services.AddSingleton<ITelegramNotificationService, TelegramNotificationService>();
builder.Services.AddSingleton<IFlightLoggingService, SqliteFlightLoggingService>();
builder.Services.AddSingleton<IRepeatVisitorService, RepeatVisitorService>();
builder.Services.AddSingleton<IFlightTrajectoryService, SqliteFlightTrajectoryService>();
builder.Services.AddSingleton<ITelegramCommandListener, TelegramCommandListener>();

// SSE
builder.Services.AddSingleton<SseBroadcaster>();

// Polling worker
builder.Services.AddHostedService<FlightTrackerWorker>();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── SSE endpoint ──────────────────────────────────────────────────────────────
var sseJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

app.MapGet("/flight-tracker/events", async (HttpContext ctx, SseBroadcaster broadcaster) =>
{
    // CORS must be set before any early return so the browser can read error responses.
    ctx.Response.Headers.AccessControlAllowOrigin = "*";

    if (!settings.Sse.Enabled)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    // Bearer token auth — accept from ?token= query param (browser SSE) or Authorization header.
    if (!string.IsNullOrEmpty(settings.Sse.BearerToken))
    {
        var queryToken  = ctx.Request.Query["token"].ToString();
        var headerToken = ctx.Request.Headers.TryGetValue("Authorization", out var authHeader)
            ? authHeader.ToString().Replace("Bearer ", "").Trim()
            : null;
        var provided = !string.IsNullOrEmpty(queryToken) ? queryToken : headerToken;

        if (provided != settings.Sse.BearerToken)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    // Tell Caddy (and nginx) not to buffer the response
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var ct = ctx.RequestAborted;

    // Flush headers immediately so the client knows the stream is open
    await ctx.Response.Body.FlushAsync(ct);

    var sub        = broadcaster.SubscribeAsync(ct);
    var enumerator = sub.GetAsyncEnumerator(ct);
    // Declared outside try so the finally can await it before disposing.
    // Calling DisposeAsync() while MoveNextAsync() is still in-flight causes
    // NotSupportedException because the iterator state machine is still running.
    Task<bool>? moveNext = null;
    try
    {
        // Interleave SSE data events with keepalive comment lines so the
        // connection stays alive through idle periods (Caddy's read timeout).
        using var ping = new PeriodicTimer(TimeSpan.FromSeconds(25));
        moveNext = enumerator.MoveNextAsync().AsTask();
        var waitPing = ping.WaitForNextTickAsync(ct).AsTask();

        while (!ct.IsCancellationRequested)
        {
            var winner = await Task.WhenAny(moveNext, waitPing);

            if (ReferenceEquals(winner, waitPing))
            {
                await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                waitPing = ping.WaitForNextTickAsync(ct).AsTask();
                continue;
            }

            // Data event
            if (!await moveNext) break;
            var json = JsonSerializer.Serialize(enumerator.Current, sseJsonOptions);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            moveNext = enumerator.MoveNextAsync().AsTask();
        }
    }
    catch (OperationCanceledException) { /* client disconnected — normal */ }
    finally
    {
        // Wait for any in-flight MoveNextAsync to finish before disposing,
        // so the iterator state machine is no longer running when DisposeAsync runs.
        if (moveNext != null)
            try { await moveNext; } catch { }
        await enumerator.DisposeAsync();
    }
});

// ── Run ───────────────────────────────────────────────────────────────────────
await app.RunAsync();
