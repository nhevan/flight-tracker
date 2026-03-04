namespace FlightTracker.Models;

/// <summary>
/// A navigational fix (waypoint, VOR, NDB) resolved from the local ARINC 424 navdata file.
/// </summary>
public enum NavFixType { Waypoint, Vor, Ndb }

public sealed record NavFix(
    string    Name,
    double    Lat,
    double    Lon,
    NavFixType Type
);
