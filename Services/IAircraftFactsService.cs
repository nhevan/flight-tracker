namespace FlightTracker.Services;

public interface IAircraftFactsService
{
    /// <summary>
    /// Returns 2-3 interesting facts about the aircraft type (e.g. seat count, year introduced, primary uses).
    /// Returns null if the feature is disabled, the type code is unknown, or the call fails.
    /// Results are cached by type code for the lifetime of the session.
    /// </summary>
    Task<string?> GetFactsAsync(string? typeCode, string? category, string? registration, CancellationToken cancellationToken);
}
