using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightTracker.Configuration;

namespace FlightTracker.Services;

public sealed class AnthropicAircraftFactsService : IAircraftFactsService
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicSettings _settings;

    // Session-lifetime cache keyed by TypeCode — same aircraft model always gives same facts
    private readonly ConcurrentDictionary<string, string?> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    // Request coalescing — concurrent callers for the same TypeCode share one Task
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight
        = new(StringComparer.OrdinalIgnoreCase);

    public AnthropicAircraftFactsService(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _settings = settings.Anthropic;
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _httpClient.BaseAddress = new Uri("https://api.anthropic.com/");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public Task<string?> GetFactsAsync(string? typeCode, string? category, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(typeCode))
            return Task.FromResult<string?>(null);

        // Hot path: return cached result immediately
        if (_cache.TryGetValue(typeCode, out var cached))
            return Task.FromResult(cached);

        // Coalesce concurrent requests for the same TypeCode
        return _inFlight.GetOrAdd(typeCode, key => FetchAndCacheAsync(key, category, cancellationToken));
    }

    private async Task<string?> FetchAndCacheAsync(string typeCode, string? category, CancellationToken cancellationToken)
    {
        try
        {
            string categoryHint = string.IsNullOrWhiteSpace(category) ? "" : $" ({category})";
            string prompt =
                $"You are a knowledgeable aviation enthusiast. Give me 2-3 interesting facts about the {typeCode} aircraft{categoryHint}. " +
                $"Must Include: approximate passenger capacity, year it first entered service, and what it is mostly used for. " +
                $"Be concise — 2-3 sentences maximum. Plain text only, no markdown, no bullet points.";

            var requestBody = new
            {
                model      = _settings.Model,
                max_tokens = _settings.MaxTokens,
                messages   = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var json    = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("v1/messages", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
                cancellationToken: cancellationToken);

            string? facts = result?.Content?.FirstOrDefault()?.Text?.Trim();
            _cache[typeCode] = facts;
            return facts;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AnthropicFacts] Failed to fetch facts for {typeCode}: {ex.Message}");
            _cache[typeCode] = null;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(typeCode, out _);
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
