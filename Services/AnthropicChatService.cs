using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightTracker.Configuration;

namespace FlightTracker.Services;

public sealed class AnthropicChatService : IAnthropicChatService
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicSettings _settings;

    private const string SystemPrompt =
        "You are the assistant for a personal flight-tracking Telegram bot. " +
        "Answer the user's message helpfully and concisely. " +
        "If the message is about aviation or flights, answer it directly. " +
        "Otherwise, politely clarify that you are a flight-tracking bot and " +
        "suggest the available commands: /stats, /spot, /spots, /range, /test. " +
        "Plain text only — no markdown, no bullet points, no asterisks.";

    public AnthropicChatService(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _settings = settings.Anthropic;
        _httpClient = httpClientFactory.CreateClient("AnthropicChat");
        _httpClient.BaseAddress = new Uri("https://api.anthropic.com/");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<string?> ChatAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return null;

        try
        {
            var requestBody = new
            {
                model      = _settings.Model,
                max_tokens = _settings.MaxTokens,
                system     = SystemPrompt,
                messages   = new[]
                {
                    new { role = "user", content = userMessage }
                }
            };

            var json    = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("v1/messages", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
                cancellationToken: cancellationToken);

            return result?.Content?.FirstOrDefault()?.Text?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnthropicChat] Failed to get reply: {ex.Message}");
            return null;
        }
    }

    // ── Private DTOs ─────────────────────────────────────────────────────────

    private sealed class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
