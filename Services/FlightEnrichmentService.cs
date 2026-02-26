namespace FlightTracker.Services;

using FlightTracker.Models;

public sealed class FlightEnrichmentService : IFlightEnrichmentService
{
    private readonly IFlightRouteService _routeService;
    private readonly IAircraftInfoService _aircraftInfoService;
    private readonly IAircraftPhotoService _photoService;
    private readonly IAircraftFactsService _factsService;

    public FlightEnrichmentService(
        IFlightRouteService routeService,
        IAircraftInfoService aircraftInfoService,
        IAircraftPhotoService photoService,
        IAircraftFactsService factsService)
    {
        _routeService        = routeService;
        _aircraftInfoService = aircraftInfoService;
        _photoService        = photoService;
        _factsService        = factsService;
    }

    public async Task<IReadOnlyList<EnrichedFlightState>> EnrichAsync(
        IReadOnlyList<FlightState> flights,
        CancellationToken cancellationToken)
    {
        // Fan out: one Task per flight, each fetching route + aircraft + photo.
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

        AircraftInfo? aircraft = aircraftTask.Result;

        // Photo and AI facts are both independent after aircraft info is ready — run in parallel.
        var photoTask = _photoService.GetPhotoUrlAsync(
            flight.Icao24, aircraft?.Registration, cancellationToken);
        var factsTask = _factsService.GetFactsAsync(
            aircraft?.TypeCode, aircraft?.Category, cancellationToken);

        await Task.WhenAll(photoTask, factsTask);

        return new EnrichedFlightState(
            State:         flight,
            Route:         routeTask.Result,
            Aircraft:      aircraft,
            PhotoUrl:      photoTask.Result,
            AircraftFacts: factsTask.Result);
    }
}
