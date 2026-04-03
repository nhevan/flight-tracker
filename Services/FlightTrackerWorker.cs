namespace FlightTracker.Services;

using System.Globalization;
using FlightTracker.Configuration;
using FlightTracker.Data;
using FlightTracker.Display;
using FlightTracker.Helpers;
using FlightTracker.Models;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Hosted background service that owns the ADS-B polling loop.
/// Extracted from Program.cs so that ASP.NET Core can run alongside it.
/// </summary>
public sealed class FlightTrackerWorker(
    AppSettings settings,
    IFlightService flightService,
    IFlightEnrichmentService enrichmentService,
    ITelegramNotificationService telegramService,
    IFlightLoggingService loggingService,
    IRepeatVisitorService repeatVisitorService,
    IFlightTrajectoryService trajectoryService,
    ITelegramCommandListener commandListener,
    SseBroadcaster sseBroadcaster) : BackgroundService
{
    private DateTimeOffset? _lastErrorNotifiedAt;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await loggingService.InitialiseAsync(stoppingToken);
        await trajectoryService.InitialiseAsync(stoppingToken);

        // ── Startup notification ───────────────────────────────────────────────
        try
        {
            var stats = await loggingService.GetStatsAsync(
                settings.HomeLocation.Latitude, settings.HomeLocation.Longitude, stoppingToken);
            await telegramService.SendStatusAsync(FormatStartupMessage(stats), stoppingToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Stats unavailable: {ex.Message}");
            await telegramService.SendStatusAsync(
                "🟢 <b>FlightTracker started</b>\n⚠️ DB stats unavailable at startup.", stoppingToken);
        }

        // ── Start Telegram command listener (background) ───────────────────────
        _ = commandListener.StartAsync(stoppingToken);

        Console.WriteLine("Flight Tracker starting. Press Ctrl+C to exit.");
        Console.WriteLine();

        TimeSpan pollInterval = TimeSpan.FromSeconds(settings.Polling.IntervalSeconds);

        var previousPositions = new Dictionary<string, (double Lat, double Lon)>(
            StringComparer.OrdinalIgnoreCase);
        var positionHistory = new Dictionary<string, List<(double Lat, double Lon)>>(
            StringComparer.OrdinalIgnoreCase);
        var notifiedIcaos = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

                var flights     = await flightService.GetOverheadFlightsAsync(stoppingToken);
                var rawEnriched = await enrichmentService.EnrichAsync(flights, stoppingToken);

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

                if (previousPositions.Count > 0)
                {
                    foreach (var ef in enriched)
                    {
                        if (!previousPositions.ContainsKey(ef.State.Icao24))
                            Console.Write('\a');
                    }
                }

                previousPositions.Clear();
                foreach (var ef in enriched)
                {
                    var fs = ef.State;
                    if (fs.Latitude.HasValue && fs.Longitude.HasValue)
                        previousPositions[fs.Icao24] = (fs.Latitude.Value, fs.Longitude.Value);
                }

                var currentIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ef in enriched)
                {
                    var fs = ef.State;
                    if (!fs.Latitude.HasValue || !fs.Longitude.HasValue) continue;
                    currentIcaos.Add(fs.Icao24);
                    if (!positionHistory.TryGetValue(fs.Icao24, out var hist))
                        positionHistory[fs.Icao24] = hist = [];
                    hist.Add((fs.Latitude.Value, fs.Longitude.Value));
                    if (hist.Count > 120) hist.RemoveAt(0);
                }
                foreach (var key in positionHistory.Keys.Except(currentIcaos).ToList())
                    positionHistory.Remove(key);

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
                    await trajectoryService.StartOrContinueAsync(ef, flightType, stoppingToken);
                }

                foreach (var icao in trajectoryService.GetActiveIcaos()
                    .Where(icao => !currentIcaos.Contains(icao)).ToList())
                {
                    await trajectoryService.CompleteSessionAsync(icao, stoppingToken);
                }

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

                        var visitorInfo = bearingChanged
                            ? null
                            : await repeatVisitorService.GetVisitorInfoAsync(f.Icao24, stoppingToken);

                        IReadOnlyList<(double Lat, double Lon)>? trajectory = bearingChanged
                            && positionHistory.TryGetValue(f.Icao24, out var hist)
                            ? hist
                            : null;

                        var recordedDots = await trajectoryService.GetRecordedPointsAsync(f.Icao24, stoppingToken);

                        await telegramService.NotifyAsync(ef, dir ?? "Towards", etaSecs, visitorInfo, stoppingToken,
                            homeLat, homeLon,
                            previousHeading: bearingChanged ? lastHeading : null,
                            trajectory: trajectory,
                            isBeingRecorded: trajectoryService.IsTracking(f.Icao24),
                            recordedDots: recordedDots.Count > 0 ? recordedDots : null,
                            isCourseChange: bearingChanged);

                        // Broadcast to SSE clients
                        if (settings.Sse.Enabled && sseBroadcaster.ClientCount > 0)
                            sseBroadcaster.Broadcast(BuildSseEvent(ef, dir ?? "Towards", etaSecs, bearingChanged));

                        if (!bearingChanged)
                            await loggingService.LogAsync(ef, dir ?? "Towards", etaSecs,
                                homeLat, homeLon, settings.HomeLocation.Name,
                                DateTimeOffset.UtcNow, stoppingToken);

                        notifiedIcaos[f.Icao24] = effectiveHeading;
                    }
                }

                foreach (var icao in notifiedIcaos.Keys
                    .Where(icao => !previousPositions.ContainsKey(icao)).ToList())
                    notifiedIcaos.Remove(icao);

                if (!Console.IsOutputRedirected)
                    FlightTableRenderer.Render(
                        enriched,
                        settings.HomeLocation.Latitude,
                        settings.HomeLocation.Longitude,
                        settings.HomeLocation.VisualRangeKm,
                        DateTimeOffset.Now);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[HTTP Error] {ex.StatusCode}: {ex.Message}");
                Console.WriteLine("Retrying on next poll...");
                await NotifyErrorAsync(
                    $"⚠️ <b>FlightTracker HTTP error</b>\n{ex.StatusCode}: {EscapeHtml(ex.Message)}",
                    stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("Retrying on next poll...");
                await NotifyErrorAsync(
                    $"⚠️ <b>FlightTracker error</b>\n{ex.GetType().Name}: {EscapeHtml(ex.Message)}",
                    stoppingToken);
            }

            try { await Task.Delay(pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("Flight Tracker stopped.");
    }

    // ── SSE event builder ─────────────────────────────────────────────────────

    private static FlightSseEvent BuildSseEvent(
        EnrichedFlightState ef,
        string direction,
        double? etaSeconds,
        bool isCourseChange)
    {
        var f    = ef.State;
        var info = ef.Aircraft;

        string? typeCode    = info?.TypeCode;
        string  typeCategory = DeriveTypeCategory(f, info);
        string? silhouette  = string.IsNullOrEmpty(typeCode)
            ? null
            : $"https://www.planespotters.net/silhouettes/{typeCode}_3.png";

        bool isEmergency = (!string.IsNullOrEmpty(f.Emergency) && f.Emergency != "none")
                           || f.Squawk is "7700" or "7600" or "7500";

        return new FlightSseEvent(
            Icao24:                    f.Icao24,
            Callsign:                  f.Callsign,
            Latitude:                  f.Latitude,
            Longitude:                 f.Longitude,
            AltitudeMeters:            f.BarometricAltitudeMeters,
            SpeedKmh:                  f.VelocityMetersPerSecond.HasValue
                                           ? f.VelocityMetersPerSecond.Value * 3.6
                                           : null,
            HeadingDegrees:            ef.EffectiveHeading,
            VerticalRateMetersPerSecond: f.VerticalRateMetersPerSecond,
            DistanceKm:                f.DistanceKm,
            EtaSeconds:                etaSeconds,
            Direction:                 direction,
            OriginIata:                ef.Route?.OriginIata,
            OriginIcao:                ef.Route?.OriginIcao,
            OriginName:                ef.Route?.OriginName,
            DestIata:                  ef.Route?.DestIata,
            DestIcao:                  ef.Route?.DestIcao,
            DestName:                  ef.Route?.DestName,
            AircraftDescription:       f.AircraftDescription,
            TypeCode:                  typeCode,
            Registration:              info?.Registration,
            Operator:                  info?.Operator,
            PlaneTypeCategory:         typeCategory,
            PhotoUrl:                  ef.PhotoUrl,
            SilhouetteUrl:             silhouette,
            IsMilitary:                f.IsMilitary,
            IsEmergency:               isEmergency,
            Emergency:                 f.Emergency == "none" ? null : f.Emergency,
            Squawk:                    f.Squawk,
            IsCourseChange:            isCourseChange,
            WindSpeedKnots:            f.WindSpeedKnots,
            WindDirectionDeg:          f.WindDirectionDeg,
            OutsideAirTempC:           f.OutsideAirTempC,
            Timestamp:                 DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Derives a simplified plane type category from the aircraft's metadata,
    /// suitable for driving UI icon/animation selection.
    /// </summary>
    private static string DeriveTypeCategory(FlightState f, AircraftInfo? info)
    {
        if (f.IsMilitary) return "military";

        string? cat  = info?.Category?.ToLowerInvariant();
        string? desc = f.AircraftDescription?.ToLowerInvariant();

        if (cat?.Contains("heli") == true)                                   return "helicopter";
        if (cat?.Contains("widebody") == true || cat == "wide-body jet")     return "widebody-jet";
        if (cat?.Contains("narrow") == true)                                 return "narrowbody-jet";
        if (cat?.Contains("turboprop") == true || cat?.Contains("prop") == true) return "turboprop";
        if (cat?.Contains("business") == true || cat?.Contains("executive") == true) return "business-jet";
        if (cat?.Contains("light") == true)                                  return "light-aircraft";

        // Fall back to description hints for common aircraft families
        if (desc is not null)
        {
            if (desc.Contains("747") || desc.Contains("777") || desc.Contains("787") ||
                desc.Contains("a330") || desc.Contains("a340") || desc.Contains("a350") || desc.Contains("a380"))
                return "widebody-jet";

            if (desc.Contains("737") || desc.Contains("a320") || desc.Contains("a321") ||
                desc.Contains("a319") || desc.Contains("a318") || desc.Contains("e170") ||
                desc.Contains("e190") || desc.Contains("crj")  || desc.Contains("embraer"))
                return "narrowbody-jet";

            if (desc.Contains("helicopter") || desc.Contains("rotor"))
                return "helicopter";
        }

        return "unknown";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task NotifyErrorAsync(string htmlMessage, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        int snoozeMins = settings.Telegram.ErrorNotificationSnoozeMinutes;
        if (_lastErrorNotifiedAt is not null &&
            (now - _lastErrorNotifiedAt.Value).TotalMinutes < snoozeMins)
            return;

        _lastErrorNotifiedAt = now;
        await telegramService.SendStatusAsync(htmlMessage, ct);
    }

    private static string FormatStartupMessage(FlightStats s)
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

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
