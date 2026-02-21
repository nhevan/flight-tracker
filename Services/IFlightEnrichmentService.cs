namespace FlightTracker.Services;

using FlightTracker.Models;

public interface IFlightEnrichmentService
{
    /// <summary>
    /// Takes a list of raw flight states and returns them paired with whatever
    /// route/aircraft metadata is available in cache or can be fetched concurrently.
    /// Never throws — enrichment failures degrade gracefully to null fields.
    /// </summary>
    Task<IReadOnlyList<EnrichedFlightState>> EnrichAsync(
        IReadOnlyList<FlightState> flights,
        CancellationToken cancellationToken);
}
