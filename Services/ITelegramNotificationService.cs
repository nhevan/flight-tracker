namespace FlightTracker.Services;

using FlightTracker.Models;

public interface ITelegramNotificationService
{
    /// <summary>
    /// Sends a Telegram message for an overhead or approaching flight.
    /// Does nothing when Telegram is disabled in config.
    /// Never throws — all errors are swallowed to keep the tracker alive.
    /// </summary>
    Task NotifyAsync(EnrichedFlightState flight, string direction, double? etaSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a plain-text status message (startup notice, error alert, etc.).
    /// Does nothing when Telegram is disabled in config.
    /// Never throws — all errors are swallowed to keep the tracker alive.
    /// </summary>
    Task SendStatusAsync(string message, CancellationToken cancellationToken = default);
}
