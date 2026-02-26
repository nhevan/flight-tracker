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
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled
            || string.IsNullOrEmpty(_settings.BotToken)
            || string.IsNullOrEmpty(_settings.ChatId))
            return;

        try
        {
            var f    = flight.State;
            string text = BuildMessage(flight, direction, etaSeconds);

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

    private static string BuildMessage(EnrichedFlightState ef, string direction, double? etaSeconds)
    {
        var f = ef.State;

        string dirEmoji = direction switch
        {
            "Overhead" => "🔴",
            "Towards"  => "🟢",
            _          => "🔵"
        };

        string callsign = string.IsNullOrWhiteSpace(f.Callsign) || f.Callsign == "N/A"
            ? f.Icao24
            : f.Callsign.Trim();

        // Route: "JNB → AMS" or "Unknown route"
        string route = (ef.Route?.OriginIata ?? ef.Route?.OriginIcao, ef.Route?.DestIata ?? ef.Route?.DestIcao) switch
        {
            (string dep, string arr) => $"{dep} → {arr}",
            _                        => "Unknown route"
        };

        // Aircraft: "B789 / PH-BHO (Wide-body Jet)" or just type/reg if partial
        string aircraft = BuildAircraftString(ef.Aircraft);

        string dist  = f.DistanceKm.HasValue
            ? f.DistanceKm.Value.ToString("F1", CultureInfo.InvariantCulture) + " km"
            : "?";
        string alt   = f.BarometricAltitudeMeters.HasValue
            ? f.BarometricAltitudeMeters.Value.ToString("F0", CultureInfo.InvariantCulture) + " m"
            : "?";
        string speed = f.VelocityMetersPerSecond.HasValue
            ? (f.VelocityMetersPerSecond.Value * 3.6).ToString("F0", CultureInfo.InvariantCulture) + " km/h"
            : "?";
        string eta   = FormatEta(etaSeconds);

        string message =
               $"{dirEmoji} <b>{EscapeHtml(callsign)}</b> — {direction}\n" +
               $"Route: {EscapeHtml(route)}\n" +
               $"Aircraft: {EscapeHtml(aircraft)}\n" +
               $"Distance: {dist} | Alt: {alt} | Speed: {speed} | Overhead in: {eta}";

        if (!string.IsNullOrWhiteSpace(ef.AircraftFacts))
            message += $"\n\n✈️ {EscapeHtml(ef.AircraftFacts)}";

        return message;
    }

    private static string FormatEta(double? etaSeconds)
    {
        if (etaSeconds is null) return "Now";
        int total = (int)etaSeconds.Value;
        int m = total / 60;
        int s = total % 60;
        return m > 0 ? $"{m}m {s:D2}s" : $"{s}s";
    }

    private static string BuildAircraftString(AircraftInfo? info)
    {
        if (info is null) return "Unknown";

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(info.TypeCode))     parts.Add(info.TypeCode);
        if (!string.IsNullOrEmpty(info.Registration)) parts.Add(info.Registration);

        string main = parts.Count > 0 ? string.Join(" / ", parts) : "Unknown";

        return !string.IsNullOrEmpty(info.Category)
            ? $"{main} ({info.Category})"
            : main;
    }

    // Escape characters that have special meaning in Telegram HTML parse mode
    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
