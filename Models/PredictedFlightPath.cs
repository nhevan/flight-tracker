namespace FlightTracker.Models;

/// <summary>
/// An ordered list of WGS-84 coordinates representing the predicted flight path
/// ahead of the aircraft, computed from the filed route string and local navdata.
/// The path already starts at (or just ahead of) the aircraft's current position.
/// When <see cref="IsDirect"/> is true the path is a straight great-circle line
/// from origin to destination (used when no filed route is available).
/// </summary>
public sealed record PredictedFlightPath(
    IReadOnlyList<(double Lat, double Lon)> Points,
    bool IsDirect = false
);
