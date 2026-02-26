namespace FlightTracker.Services;

public interface ITelegramCommandListener
{
    /// <summary>
    /// Starts a long-polling loop that listens for incoming Telegram messages
    /// and handles recognised commands (e.g. "stats" / "/stats").
    /// Runs until the cancellation token is cancelled.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
}
