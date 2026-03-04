namespace FlightTracker.Models;

/// <summary>
/// The filed flight route string as returned by FlightAware AeroAPI,
/// e.g. "BERGI1A ARNEM UL851 BEGAR DCT LOGAN LOGA2R".
/// </summary>
public sealed record FiledRoute(
    string Callsign,
    string RouteString
);
