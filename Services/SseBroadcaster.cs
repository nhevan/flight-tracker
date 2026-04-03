namespace FlightTracker.Services;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FlightTracker.Models;

/// <summary>
/// Singleton that fans out FlightSseEvent notifications to all connected SSE clients.
/// Each connected client owns one unbounded channel; Broadcast writes to all of them.
/// Channels are removed automatically when the client disconnects (cancellation in ReadUntilCancelledAsync).
/// </summary>
public sealed class SseBroadcaster
{
    private readonly List<Channel<FlightSseEvent>> _clients = [];
    private readonly object _lock = new();

    /// <summary>Number of currently connected SSE clients.</summary>
    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    /// <summary>
    /// Returns an async stream of events for one SSE client.
    /// The channel is created here and removed when <paramref name="ct"/> is cancelled
    /// (i.e. the HTTP request is aborted / the client disconnects).
    /// </summary>
    public IAsyncEnumerable<FlightSseEvent> SubscribeAsync(CancellationToken ct) =>
        ReadUntilCancelledAsync(CreateChannel(), ct);

    /// <summary>Writes <paramref name="evt"/> to every connected client's channel.</summary>
    public void Broadcast(FlightSseEvent evt)
    {
        lock (_lock)
            foreach (var ch in _clients)
                ch.Writer.TryWrite(evt);
    }

    private Channel<FlightSseEvent> CreateChannel()
    {
        var ch = Channel.CreateUnbounded<FlightSseEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_lock) _clients.Add(ch);
        return ch;
    }

    private async IAsyncEnumerable<FlightSseEvent> ReadUntilCancelledAsync(
        Channel<FlightSseEvent> ch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            // Remove this client's channel whether the loop ended normally or was cancelled
            lock (_lock) _clients.Remove(ch);
        }
    }
}
