namespace FlightTracker.Services;

public interface IAnthropicChatService
{
    /// <summary>
    /// Forwards <paramref name="userMessage"/> to the Anthropic API and returns
    /// the assistant's plain-text reply, or null on failure / when disabled.
    /// </summary>
    Task<string?> ChatAsync(string userMessage, CancellationToken cancellationToken = default);
}
