using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightTracker.Configuration;
using FlightTracker.Data;
using FlightTracker.Models;

namespace FlightTracker.Services;

/// <summary>
/// Long-polls the Telegram Bot API for incoming messages.
/// Handles "stats" / "/stats" commands by replying with aggregated flight statistics.
/// </summary>
public sealed class TelegramCommandListener : ITelegramCommandListener
{
    private readonly TelegramSettings _settings;
    private readonly HomeLocationSettings _homeLocation;
    private readonly IFlightLoggingService _loggingService;
    private readonly ITelegramNotificationService _notificationService;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TelegramCommandListener(
        AppSettings settings,
        IFlightLoggingService loggingService,
        IHttpClientFactory httpClientFactory,
        ITelegramNotificationService notificationService)
    {
        _settings             = settings.Telegram;
        _homeLocation         = settings.HomeLocation;
        _loggingService       = loggingService;
        _notificationService  = notificationService;
        _httpClient           = httpClientFactory.CreateClient("TelegramListener");
        _httpClient.Timeout   = TimeSpan.FromSeconds(40); // > long-poll timeout
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled
            || string.IsNullOrEmpty(_settings.BotToken)
            || string.IsNullOrEmpty(_settings.ChatId))
        {
            return;
        }

        // Clear any stale webhook — Telegram forbids simultaneous webhook + long-polling (causes 409)
        await DeleteWebhookAsync(cancellationToken);

        long offset = 0;
        Console.WriteLine("[TelegramListener] Listening for commands (/stats, /spot, /test)...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string url = $"https://api.telegram.org/bot{_settings.BotToken}" +
                             $"/getUpdates?offset={offset}&timeout=30&allowed_updates=[\"message\"]";

                using var httpResponse = await _httpClient.GetAsync(url, cancellationToken);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    string errBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(errBody, null, httpResponse.StatusCode);
                }

                var response = await httpResponse.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(
                    JsonOptions, cancellationToken);

                if (response?.Result is null) continue;

                foreach (var update in response.Result)
                {
                    offset = update.UpdateId + 1;

                    string? text = update.Message?.Text?.Trim();
                    if (text is null) continue;

                    if (text.Equals("stats", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("/stats", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleStatsCommandAsync(update.Message!.Chat.Id, cancellationToken);
                    }
                    else if (text.StartsWith("/spot", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleSpotCommandAsync(update.Message!.Chat.Id, text, cancellationToken);
                    }
                    else if (text.Equals("/test", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleTestCommandAsync(update.Message!.Chat.Id, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Telegram returns 409 for two distinct reasons — handle each differently.
                if (ex.Message.Contains("terminated by other getUpdates"))
                {
                    // Another bot instance is actively long-polling the same token.
                    // deleteWebhook is a no-op here; only stopping the other process helps.
                    Console.WriteLine("[TelegramListener] 409 — another instance is already polling " +
                                      "this bot token. Check for duplicate processes on EC2: " +
                                      "`ps aux | grep dotnet`. Backing off 60s...");
                    try { await Task.Delay(60_000, cancellationToken); } catch { break; }
                }
                else
                {
                    // A webhook is set (externally or from a previous deployment).
                    // deleteWebhook clears it; retry after 30s.
                    Console.WriteLine($"[TelegramListener] 409 — webhook is active ({ex.Message}). " +
                                      "Clearing webhook and retrying in 30s...");
                    await DeleteWebhookAsync(cancellationToken);
                    try { await Task.Delay(30_000, cancellationToken); } catch { break; }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramListener] Error: {ex.Message} — retrying in 5s");
                try { await Task.Delay(5_000, cancellationToken); } catch { break; }
            }
        }
    }

    private async Task DeleteWebhookAsync(CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/deleteWebhook";
            using var response = await _httpClient.PostAsync(url, content: null, cancellationToken);
            if (response.IsSuccessStatusCode)
                Console.WriteLine("[TelegramListener] Webhook cleared (long-polling ready).");
            else
                Console.WriteLine($"[TelegramListener] deleteWebhook returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TelegramListener] Could not clear webhook: {ex.Message}");
        }
    }

    private async Task HandleStatsCommandAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            FlightStats stats = await _loggingService.GetStatsAsync(
                _homeLocation.Latitude, _homeLocation.Longitude, cancellationToken);
            string message    = FormatStatsMessage(stats, _homeLocation.Name);

            string apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
            var payload   = new
            {
                chat_id    = chatId.ToString(),
                text       = message,
                parse_mode = "HTML"
            };

            using var response = await _httpClient.PostAsJsonAsync(apiUrl, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[TelegramListener] Failed to send stats: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TelegramListener] Error sending stats: {ex.Message}");
        }
    }

    private async Task HandleSpotCommandAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var ic = CultureInfo.InvariantCulture;

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendMessageAsync(chatId,
                "Usage: /spot &lt;lat&gt; &lt;lon&gt; [name]\n" +
                "Example: /spot 51.977 4.617 Home Garden",
                cancellationToken);
            return;
        }

        if (!double.TryParse(parts[1], NumberStyles.Float, ic, out double lat) || lat < -90 || lat > 90)
        {
            await SendMessageAsync(chatId,
                $"❌ Invalid latitude <code>{EscapeHtml(parts[1])}</code> — must be a number between −90 and 90.",
                cancellationToken);
            return;
        }

        if (!double.TryParse(parts[2], NumberStyles.Float, ic, out double lon) || lon < -180 || lon > 180)
        {
            await SendMessageAsync(chatId,
                $"❌ Invalid longitude <code>{EscapeHtml(parts[2])}</code> — must be a number between −180 and 180.",
                cancellationToken);
            return;
        }

        string? name = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;

        _homeLocation.Latitude             = lat;
        _homeLocation.Longitude            = lon;
        _homeLocation.Name                 = name;
        _homeLocation.LocationResetRequested = true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📍 <b>Spotting location updated</b>");
        sb.AppendLine();
        if (name is not null)
            sb.AppendLine($"<b>{EscapeHtml(name)}</b>");
        sb.AppendLine($"Lat: {lat.ToString("F6", ic)} · Lon: {lon.ToString("F6", ic)}");
        sb.AppendLine();
        sb.Append("Notification state reset — flights near this spot will notify fresh.");

        await SendMessageAsync(chatId, sb.ToString(), cancellationToken);
        Console.WriteLine($"[TelegramListener] /spot command: location set to ({lat:F6}, {lon:F6})" +
                          (name is not null ? $" \"{name}\"" : ""));
    }

    private async Task HandleTestCommandAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            // Place the test plane ~15 km north of home heading due south (toward home)
            double testLat = _homeLocation.Latitude + 0.135;
            double testLon = _homeLocation.Longitude;
            double distKm  = Haversine.DistanceKm(
                testLat, testLon, _homeLocation.Latitude, _homeLocation.Longitude);

            var state = new FlightState
            {
                Icao24                      = "aabbcc",
                Callsign                    = "TST1234",
                OriginCountry               = "Netherlands",
                Latitude                    = testLat,
                Longitude                   = testLon,
                BarometricAltitudeMeters    = 6000,
                OnGround                    = false,
                VelocityMetersPerSecond     = 230,      // ≈ 828 km/h
                HeadingDegrees              = 180,      // due south
                VerticalRateMetersPerSecond = -5,
                DistanceKm                  = distKm,
                AircraftDescription         = "Airbus A320-200",
                WindDirectionDeg            = 270,
                WindSpeedKnots              = 15,
                OutsideAirTempC             = -25,
            };

            var route = new FlightRoute(
                OriginIcao: "EHAM", OriginIata: "AMS",
                OriginName: "Amsterdam Airport Schiphol",
                OriginLat: 52.308, OriginLon: 4.764,
                DestIcao:  "EGLL", DestIata:  "LHR",
                DestName:  "London Heathrow Airport",
                DestLat:   51.477, DestLon:  -0.461,
                RouteDistanceKm: 370);

            var aircraft = new AircraftInfo(
                TypeCode:     "A320",
                Registration: "PH-TEST",
                Operator:     "Test Airways",
                Category:     "Narrow-body Jet");

            var flight = new EnrichedFlightState(
                State:         state,
                Route:         route,
                Aircraft:      aircraft,
                PhotoUrl:      null,    // exercises the map-only send path
                AircraftFacts: null);

            double etaSeconds = distKm * 1000.0 / state.VelocityMetersPerSecond!.Value;

            await _notificationService.NotifyAsync(
                flight, "Towards", etaSeconds, visitorInfo: null, cancellationToken);

            await SendMessageAsync(chatId, "✅ Test notification sent!", cancellationToken);
            Console.WriteLine("[TelegramListener] /test command: synthetic notification sent.");
        }
        catch (Exception ex)
        {
            await SendMessageAsync(chatId, $"❌ Test failed: {EscapeHtml(ex.Message)}", cancellationToken);
            Console.WriteLine($"[TelegramListener] /test error: {ex.Message}");
        }
    }

    private async Task SendMessageAsync(long chatId, string html, CancellationToken cancellationToken)
    {
        try
        {
            string apiUrl = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
            var payload   = new { chat_id = chatId.ToString(), text = html, parse_mode = "HTML" };
            using var response = await _httpClient.PostAsJsonAsync(apiUrl, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[TelegramListener] Failed to send message: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TelegramListener] Error sending message: {ex.Message}");
        }
    }

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string FormatStatsMessage(FlightStats s, string? locationName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📊 <b>Flight Tracker Stats</b>");
        if (locationName is not null)
            sb.AppendLine($"📍 {EscapeHtml(locationName)}");
        sb.AppendLine();

        sb.AppendLine($"✈️ Total planes tracked: <b>{s.TotalSightings:N0}</b>");
        sb.AppendLine($"📅 Today: <b>{s.TodayCount}</b> planes ({s.TodayUniqueAircraft} unique)");

        if (s.BusiestHour.HasValue)
        {
            string busyLabel = $"{s.BusiestHour:D2}:00–{s.BusiestHour + 1:D2}:00";
            string avgPart   = s.BusiestHourAvgPerDay.HasValue
                ? $" (avg {s.BusiestHourAvgPerDay.Value.ToString("F1", CultureInfo.InvariantCulture)}/day)"
                : "";
            sb.AppendLine($"🏆 Busiest hour: <b>{busyLabel}</b>{avgPart}");
        }

        if (s.MostSpottedAirline is not null)
            sb.AppendLine($"🛫 Most spotted airline: <b>{s.MostSpottedAirline}</b> ({s.MostSpottedAirlineCount} sightings)");

        if (s.RarestTypeCode is not null)
            sb.AppendLine($"🦄 Rarest aircraft type: <b>{s.RarestTypeCode}</b> ({s.RarestTypeCount} sighting{(s.RarestTypeCount == 1 ? "" : "s")})");

        if (s.LongestGap.HasValue && s.LongestGapStart.HasValue && s.LongestGapEnd.HasValue)
        {
            string gapDuration = FormatDuration(s.LongestGap.Value);
            string gapStart    = s.LongestGapStart.Value.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            string gapEnd      = s.LongestGapEnd.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            sb.AppendLine($"⏱️ Longest gap: <b>{gapDuration}</b> ({gapStart}–{gapEnd})");
        }

        if (s.CurrentStreakHours > 0)
            sb.AppendLine($"🔥 Current streak: <b>{s.CurrentStreakHours}</b> consecutive hour{(s.CurrentStreakHours == 1 ? "" : "s")} with planes");
        else
            sb.AppendLine("🔥 Current streak: <b>0</b> (no planes in the current hour yet)");

        return sb.ToString().TrimEnd();
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        return $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    // ── Private Telegram DTOs ─────────────────────────────────────────────────

    private sealed class TelegramUpdatesResponse
    {
        [JsonPropertyName("result")]
        public List<TelegramUpdate>? Result { get; set; }
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; set; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("chat")]
        public TelegramChat Chat { get; set; } = new();
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
