namespace FlightTracker.Services;

using System.Globalization;
using System.Net.Http.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

/// <summary>
/// Sends a Telegram message via the Bot API when a flight is Overhead or Towards home.
/// Uses a plain HttpClient — no third-party Telegram SDK required.
/// </summary>
public sealed class TelegramNotificationService : ITelegramNotificationService
{
    private readonly TelegramSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly IMapSnapshotService _mapService;

    // Telegram captions are capped at 1,024 characters
    private const int TelegramCaptionLimit = 1024;

    public TelegramNotificationService(
        AppSettings settings,
        IHttpClientFactory httpClientFactory,
        IMapSnapshotService mapService)
    {
        _settings   = settings.Telegram;
        _mapService = mapService;
        _httpClient = httpClientFactory.CreateClient("telegram");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task NotifyAsync(
        EnrichedFlightState flight,
        string direction,
        double? etaSeconds,
        RepeatVisitorInfo? visitorInfo,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled
            || string.IsNullOrEmpty(_settings.BotToken)
            || string.IsNullOrEmpty(_settings.ChatId))
            return;

        try
        {
            var f    = flight.State;
            string text = BuildMessage(flight, direction, etaSeconds, visitorInfo);

            // 1️⃣ Try to get a live map snapshot (fetched server-side to keep token private)
            byte[]? mapBytes = await _mapService.GetSnapshotAsync(
                f.Latitude, f.Longitude, f.HeadingDegrees,
                f.BarometricAltitudeMeters, cancellationToken);

            string apiUrl;
            HttpContent requestContent;

            string truncatedText = text.Length > TelegramCaptionLimit
                ? text[..(TelegramCaptionLimit - 1)] + "…"
                : text;

            if (mapBytes is not null && !string.IsNullOrEmpty(flight.PhotoUrl))
            {
                // Both map and aircraft photo — send as a 2-photo album via sendMediaGroup.
                // Caption goes on the first item (the map); aircraft photo is second, captionless.
                apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendMediaGroup";

                var mediaArray = System.Text.Json.JsonSerializer.Serialize(new object[]
                {
                    new { type = "photo", media = "attach://map", caption = truncatedText, parse_mode = "HTML" },
                    new { type = "photo", media = flight.PhotoUrl }
                });

                var form = new MultipartFormDataContent();
                form.Add(new StringContent(_settings.ChatId), "chat_id");
                form.Add(new StringContent(mediaArray),        "media");
                form.Add(new ByteArrayContent(mapBytes),       "map", "map.png");
                requestContent = form;
            }
            else if (mapBytes is not null)
            {
                // Map only — upload as multipart/form-data so the Mapbox token stays private
                apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendPhoto";

                var form = new MultipartFormDataContent();
                form.Add(new StringContent(_settings.ChatId),  "chat_id");
                form.Add(new ByteArrayContent(mapBytes),        "photo", "map.png");
                form.Add(new StringContent(truncatedText),      "caption");
                form.Add(new StringContent("HTML"),             "parse_mode");
                requestContent = form;
            }
            else if (!string.IsNullOrEmpty(flight.PhotoUrl))
            {
                // No map but planespotters photo available — URL-based sendPhoto (existing behaviour)
                apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendPhoto";
                var payload = new
                {
                    chat_id    = _settings.ChatId,
                    photo      = flight.PhotoUrl,
                    caption    = text,
                    parse_mode = "HTML"
                };
                requestContent = JsonContent.Create(payload);
            }
            else
            {
                // No photo of any kind — plain text message
                apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
                var payload = new
                {
                    chat_id    = _settings.ChatId,
                    text,
                    parse_mode = "HTML"
                };
                requestContent = JsonContent.Create(payload);
            }

            using var response = await _httpClient.PostAsync(apiUrl, requestContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Telegram] Warning: {(int)response.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            // Notification failure must never crash the tracker
            Console.WriteLine($"[Telegram] Error: {ex.Message}");
        }
    }

    private static string BuildMessage(
        EnrichedFlightState ef,
        string direction,
        double? etaSeconds,
        RepeatVisitorInfo? visitorInfo)
    {
        var f = ef.State;

        string callsign = string.IsNullOrWhiteSpace(f.Callsign) || f.Callsign == "N/A"
            ? f.Icao24
            : f.Callsign.Trim();

        var sb = new System.Text.StringBuilder();

        // ── Repeat visitor banner (prepended before everything else) ─────────
        // Use registration for a friendlier display name; fall back to ICAO24 hex.
        string displayIdent = !string.IsNullOrEmpty(ef.Aircraft?.Registration)
            ? ef.Aircraft!.Registration!
            : f.Icao24;

        if (visitorInfo is not null)
        {
            string relTime = FormatRelativeTime(visitorInfo.LastSeenAt);
            string destHint = !string.IsNullOrEmpty(visitorInfo.LastDestIata)
                ? $" heading to {visitorInfo.LastDestIata}"
                : string.Empty;
            int visitNumber = visitorInfo.TotalPreviousSightings + 1;
            sb.AppendLine($"🔁 Welcome back! {EscapeHtml(displayIdent)} was last seen {relTime}{EscapeHtml(destHint)}. This is visit #{visitNumber}!");
        }
        else
        {
            sb.AppendLine($"👋 First time spotting this aircraft!");
        }

        // ── Header line ──────────────────────────────────────────────────────
        bool isEmergency = (!string.IsNullOrEmpty(f.Emergency) && f.Emergency != "none")
                           || f.Squawk is "7700" or "7600" or "7500";

        string headerEmoji = isEmergency  ? "🚨"
                           : f.IsMilitary ? "🪖"
                           : direction switch
                             {
                                 "Overhead" => "🔴",
                                 "Towards"  => "🟢",
                                 _          => "🔵"
                             };

        string etaStr = FormatEta(etaSeconds);
        string header = isEmergency
            ? $"{headerEmoji} <b>{EscapeHtml(callsign)} — EMERGENCY</b>"
            : $"{headerEmoji} <b>{EscapeHtml(callsign)} — {direction}</b> | {etaStr}";

        sb.AppendLine(header);

        // Emergency detail line
        if (isEmergency)
        {
            string emergencyLabel = f.Emergency switch
            {
                "general"   => "General emergency",
                "lifeguard" => "Medical emergency",
                "minfuel"   => "Minimum fuel",
                "nordo"     => "No radio contact",
                "unlawful"  => "Unlawful interference",
                "downed"    => "Downed aircraft",
                _           => "Emergency"
            };
            string squawkNote = !string.IsNullOrEmpty(f.Squawk) ? $" · Squawk {f.Squawk}" : string.Empty;
            sb.AppendLine($"⚠️ {emergencyLabel}{squawkNote} | ETA: {etaStr}");
        }

        // ── Route line ───────────────────────────────────────────────────────
        if (ef.Route?.OriginIata is not null || ef.Route?.OriginIcao is not null
            || ef.Route?.DestIata is not null || ef.Route?.DestIcao  is not null)
        {
            string originCode = ef.Route!.OriginIata ?? ef.Route.OriginIcao ?? "?";
            string destCode   = ef.Route.DestIata    ?? ef.Route.DestIcao   ?? "?";

            // Prefer full airport name; fall back to code alone
            string originLabel = string.IsNullOrEmpty(ef.Route.OriginName)
                ? originCode
                : $"{ef.Route.OriginName} ({originCode})";
            string destLabel = string.IsNullOrEmpty(ef.Route.DestName)
                ? destCode
                : $"{ef.Route.DestName} ({destCode})";

            // ETA to destination: remaining straight-line distance ÷ current speed
            string etaToDest = string.Empty;
            if (ef.Route.DestLat.HasValue && ef.Route.DestLon.HasValue
                && f.Latitude.HasValue    && f.Longitude.HasValue
                && f.VelocityMetersPerSecond is > 0)
            {
                double remainKm = Haversine.DistanceKm(
                    f.Latitude.Value,       f.Longitude.Value,
                    ef.Route.DestLat.Value, ef.Route.DestLon.Value);
                double etaMins = remainKm * 1000.0 / f.VelocityMetersPerSecond.Value / 60.0;
                etaToDest = etaMins < 60
                    ? $" · arriving in ~{(int)Math.Round(etaMins)} mins"
                    : $" · arriving in ~{(int)(etaMins / 60)}h {(int)(etaMins % 60)}m";
            }

            sb.AppendLine(EscapeHtml($"{originLabel} → {destLabel}{etaToDest}"));
        }
        else
        {
            sb.AppendLine("Unknown route");
        }

        // ── Aircraft line ────────────────────────────────────────────────────
        sb.AppendLine(EscapeHtml(BuildAircraftString(f.AircraftDescription, ef.Aircraft)));

        // ── Stats line ───────────────────────────────────────────────────────
        string alt   = f.BarometricAltitudeMeters.HasValue
            ? f.BarometricAltitudeMeters.Value.ToString("F0", CultureInfo.InvariantCulture) + " m"
            : "?";
        string speed = f.VelocityMetersPerSecond.HasValue
            ? (f.VelocityMetersPerSecond.Value * 3.6).ToString("F0", CultureInfo.InvariantCulture) + " km/h"
            : "?";
        string dist  = f.DistanceKm.HasValue
            ? f.DistanceKm.Value.ToString("F1", CultureInfo.InvariantCulture) + " km"
            : "?";

        // Selected altitude indicator: show when nav altitude differs from current by > 300 m
        string navAlt = string.Empty;
        if (f.NavAltitudeMeters.HasValue && f.BarometricAltitudeMeters.HasValue
            && Math.Abs(f.NavAltitudeMeters.Value - f.BarometricAltitudeMeters.Value) > 300)
        {
            int fl = (int)Math.Round(f.NavAltitudeMeters.Value / 30.48 / 10) * 10;
            navAlt = $" → FL{fl:D3}";
        }

        sb.AppendLine($"Alt: {alt}{navAlt} | Speed: {speed} | {dist}" +
                      (isEmergency ? string.Empty : $" | ETA: {etaStr}"));

        // ── Wind / temp line (only when data is available) ───────────────────
        var windParts = new List<string>(2);
        if (f.WindSpeedKnots.HasValue && f.WindDirectionDeg.HasValue)
            windParts.Add($"Wind: {f.WindDirectionDeg.Value:F0}° {f.WindSpeedKnots.Value:F0} kts");
        else if (f.WindSpeedKnots.HasValue)
            windParts.Add($"Wind: {f.WindSpeedKnots.Value:F0} kts");
        if (f.OutsideAirTempC.HasValue)
            windParts.Add($"OAT: {f.OutsideAirTempC.Value:F0}°C");
        if (windParts.Count > 0)
            sb.AppendLine(string.Join(" | ", windParts));

        // ── AI facts ─────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(ef.AircraftFacts))
            sb.Append($"\n✈️ {EscapeHtml(ef.AircraftFacts)}");

        return sb.ToString().TrimEnd();
    }

    private static string FormatEta(double? etaSeconds)
    {
        if (etaSeconds is null) return "Now";
        int total = (int)etaSeconds.Value;
        int m = total / 60;
        int s = total % 60;
        return m > 0 ? $"{m}m {s:D2}s" : $"{s}s";
    }

    private static string FormatRelativeTime(DateTimeOffset lastSeen)
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - lastSeen;
        return elapsed.TotalMinutes < 2    ? "just now"
             : elapsed.TotalMinutes < 60   ? $"{(int)elapsed.TotalMinutes} minutes ago"
             : elapsed.TotalHours   < 24   ? $"{(int)elapsed.TotalHours} hours ago"
             : elapsed.TotalDays    < 2    ? "yesterday"
             : elapsed.TotalDays    < 7    ? $"{(int)elapsed.TotalDays} days ago"
             : elapsed.TotalDays    < 14   ? "last week"
             : $"{(int)(elapsed.TotalDays / 7)} weeks ago";
    }

    private static string BuildAircraftString(string? description, AircraftInfo? info)
    {
        // Lead with the full description from airplanes.live (e.g. "Boeing 787-9"),
        // then type code / registration, then operator.
        var parts = new List<string>(4);

        string? leadDesc = !string.IsNullOrEmpty(description) ? description
                         : !string.IsNullOrEmpty(info?.Category) ? info!.Category
                         : null;
        if (leadDesc is not null) parts.Add(leadDesc);

        var codeParts = new List<string>(2);
        if (!string.IsNullOrEmpty(info?.TypeCode))     codeParts.Add(info!.TypeCode);
        if (!string.IsNullOrEmpty(info?.Registration)) codeParts.Add(info!.Registration);
        if (codeParts.Count > 0) parts.Add(string.Join(" / ", codeParts));

        if (!string.IsNullOrEmpty(info?.Operator)) parts.Add(info!.Operator);

        return parts.Count > 0 ? string.Join(" · ", parts) : "Unknown aircraft";
    }

    public async Task SendStatusAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled
            || string.IsNullOrEmpty(_settings.BotToken)
            || string.IsNullOrEmpty(_settings.ChatId))
            return;

        try
        {
            var apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
            var payload = new
            {
                chat_id    = _settings.ChatId,
                text       = message,
                parse_mode = "HTML"
            };

            using var response = await _httpClient.PostAsync(
                apiUrl, JsonContent.Create(payload), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Telegram] Status send warning: {(int)response.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Telegram] Status send error: {ex.Message}");
        }
    }

    // Escape characters that have special meaning in Telegram HTML parse mode
    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
