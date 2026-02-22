namespace FlightTracker.Services;

using FlightTracker.Models;

public sealed class FlightEnrichmentService : IFlightEnrichmentService
{
    private readonly IFlightRouteService _routeService;
    private readonly IAircraftInfoService _aircraftInfoService;
    private readonly IAircraftPhotoService _photoService;

    public FlightEnrichmentService(
        IFlightRouteService routeService,
        IAircraftInfoService aircraftInfoService,
        IAircraftPhotoService photoService)
    {
        _routeService        = routeService;
        _aircraftInfoService = aircraftInfoService;
        _photoService        = photoService;
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

        // Photo lookup: try hex first, fall back to registration inside the service.
        // We await aircraft first so we can pass the registration for the fallback.
        string? photoUrl = await _photoService.GetPhotoUrlAsync(
            flight.Icao24,
            aircraft?.Registration,
            cancellationToken);

        return new EnrichedFlightState(
            State:    flight,
            Route:    routeTask.Result,
            Aircraft: aircraft,
            PhotoUrl: photoUrl);
    }
}
