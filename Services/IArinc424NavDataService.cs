namespace FlightTracker.Services;

using FlightTracker.Models;

/// <summary>
/// Provides lat/lon lookups for navigational fixes (waypoints, VORs, NDBs)
/// parsed from the local open-flightmaps ARINC 424 data files.
/// </summary>
public interface IArinc424NavDataService
{
    /// <summary>
    /// Attempts to resolve a fix identifier (e.g. "ARNEM", "AMS", "SPL") to coordinates.
    /// Returns null when the fix is not in the local navdata file.
    /// When multiple fixes share the same name, the one closest to
    /// <paramref name="hintLat"/>/<paramref name="hintLon"/> is returned.
    /// </summary>
    NavFix? TryResolveFix(string name, double hintLat = 52.0, double hintLon = 4.5);
}
