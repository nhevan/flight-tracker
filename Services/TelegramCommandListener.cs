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
    private readonly IFlightLoggingService _loggingService;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TelegramCommandListener(
        AppSettings settings,
        IFlightLoggingService loggingService,
        IHttpClientFactory httpClientFactory)
    {
        _settings       = settings.Telegram;
        _loggingService = loggingService;
        _httpClient     = httpClientFactory.CreateClient("TelegramListener");
        _httpClient.Timeout = TimeSpan.FromSeconds(40); // > long-poll timeout
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
        Console.WriteLine("[TelegramListener] Listening for commands (\"stats\" or \"/stats\")...");

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
            FlightStats stats = await _loggingService.GetStatsAsync(cancellationToken);
            string message    = FormatStatsMessage(stats);

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

    private static string FormatStatsMessage(FlightStats s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📊 <b>Flight Tracker Stats</b>");
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
