namespace FlightTracker.Services;

using System.Text.Json;
using FlightTracker.Configuration;
using FlightTracker.Models;

public sealed class OpenSkyTokenProvider : IOpenSkyTokenProvider
{
    private readonly HttpClient _authClient;
    private readonly AppSettings _settings;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenSkyTokenProvider(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _settings = settings;
        _authClient = httpClientFactory.CreateClient("opensky-auth");
        _authClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public void Invalidate()
    {
        _cachedToken = null;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Return cached token if it won't expire in the next 60 seconds
            if (_cachedToken is not null &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromSeconds(60))
            {
                return _cachedToken;
            }

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _settings.OpenSky.ClientId,
                ["client_secret"] = _settings.OpenSky.ClientSecret,
            });

            using HttpResponseMessage tokenResponse =
                await _authClient.PostAsync(_settings.OpenSky.TokenUrl, body, cancellationToken);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                string errBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Token endpoint returned {(int)tokenResponse.StatusCode}: {errBody}",
                    null, tokenResponse.StatusCode);
            }

            await using Stream tokenStream =
                await tokenResponse.Content.ReadAsStreamAsync(cancellationToken);

            var tokenData = await JsonSerializer.DeserializeAsync<TokenResponse>(
                tokenStream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Token endpoint returned an empty or invalid response.");

            _cachedToken = tokenData.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenData.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
