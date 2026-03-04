namespace FlightTracker.Services;

/// <summary>
/// Looks up crowd-sourced filed flight plans from FlightPlanDatabase.com.
/// Returns an ordered list of (lat, lon) waypoints for the given origin→destination
/// pair, or null when no suitable plan exists (caller should fall back to
/// SQLite airway snapping).
/// </summary>
public interface IFlightPlanDBService
{
    /// <summary>
    /// Returns the waypoints of the most popular filed plan for the given
    /// origin→destination pair, including the departure and arrival airports.
    /// Returns null when no plan with ≥ 6 waypoints is found.
    /// Results are cached by (fromIcao, toIcao) for the lifetime of the service.
    /// </summary>
    Task<FlightPlanResult?> GetRouteAsync(
        string fromIcao, string toIcao,
        CancellationToken ct = default);
}

/// <param name="Points">Ordered waypoints from origin to destination.</param>
/// <param name="PlanId">FlightPlanDB plan identifier (for logging).</param>
/// <param name="Airways">Distinct airway names used in the plan (for nav log display).</param>
public sealed record FlightPlanResult(
    IReadOnlyList<(double Lat, double Lon)> Points,
    int PlanId,
    IReadOnlyList<string> Airways);
