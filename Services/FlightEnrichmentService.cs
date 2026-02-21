namespace FlightTracker.Services;

using FlightTracker.Models;

public sealed class FlightEnrichmentService : IFlightEnrichmentService
{
    private readonly IFlightRouteService _routeService;
    private readonly IAircraftInfoService _aircraftInfoService;

    public FlightEnrichmentService(
        IFlightRouteService routeService,
        IAircraftInfoService aircraftInfoService)
    {
        _routeService = routeService;
        _aircraftInfoService = aircraftInfoService;
    }

    public async Task<IReadOnlyList<EnrichedFlightState>> EnrichAsync(
        IReadOnlyList<FlightState> flights,
        CancellationToken cancellationToken)
    {
        // Fan out: one Task per flight, each fetching route + aircraft concurrently.
        var tasks = flights.Select(f => EnrichOneAsync(f, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<EnrichedFlightState> EnrichOneAsync(
        FlightState flight,
        CancellationToken cancellationToken)
    {
        // Route and aircraft info are independent — fetch both in parallel.
        // Route is keyed by callsign (adsbdb); aircraft info by ICAO24 hex (hexdb).
        var routeTask    = _routeService.GetRouteAsync(flight.Callsign, cancellationToken);
        var aircraftTask = _aircraftInfoService.GetAircraftInfoAsync(flight.Icao24, cancellationToken);

        await Task.WhenAll(routeTask, aircraftTask);

        return new EnrichedFlightState(
            State:    flight,
            Route:    routeTask.Result,
            Aircraft: aircraftTask.Result);
    }
}
