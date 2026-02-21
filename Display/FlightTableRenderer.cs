namespace FlightTracker.Display;

using System.Globalization;
using FlightTracker.Models;
using Spectre.Console;

public static class FlightTableRenderer
{
    public static void Render(
        IReadOnlyList<EnrichedFlightState> flights,
        double homeLat,
        double homeLon,
        double visualRangeKm,
        DateTimeOffset timestamp)
    {
        AnsiConsole.Clear();

        string rangeLabel = visualRangeKm > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Range: [magenta]{visualRangeKm:F0} km[/]  ")
            : string.Empty;

        AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"[bold cyan]Flight Tracker[/]  " +
            $"Home: [yellow]{homeLat:F4}, {homeLon:F4}[/]  " +
            $"{rangeLabel}" +
            $"Last poll: [green]{timestamp:HH:mm:ss}[/]  " +
            $"Overhead: [white]{flights.Count}[/]"));

        AnsiConsole.WriteLine();

        if (flights.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No flights detected in visual range.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Dist (km)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Callsign[/]").Centered())
            .AddColumn(new TableColumn("[bold]ICAO24[/]").Centered())
            .AddColumn(new TableColumn("[bold]Country[/]"))
            .AddColumn(new TableColumn("[bold]Route[/]").Centered())
            .AddColumn(new TableColumn("[bold]Aircraft[/]").Centered())
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Alt (m)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Speed (km/h)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Heading[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Direction[/]").Centered())
            .AddColumn(new TableColumn("[bold]V/Rate (m/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]ETE[/]").RightAligned());

        // Sort by distance (closest first); flights with unknown position go last
        foreach (var ef in flights.OrderBy(ef => ef.State.DistanceKm ?? double.MaxValue))
        {
            var f = ef.State;

            string distance = f.DistanceKm.HasValue
                ? f.DistanceKm.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "[grey]--[/]";

            string altitude = f.BarometricAltitudeMeters.HasValue
                ? f.BarometricAltitudeMeters.Value.ToString("F0", CultureInfo.InvariantCulture)
                : "[grey]--[/]";

            string speed = f.VelocityMetersPerSecond.HasValue
                ? (f.VelocityMetersPerSecond.Value * 3.6).ToString("F0", CultureInfo.InvariantCulture)
                : "[grey]--[/]";

            string heading = f.HeadingDegrees.HasValue
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{f.HeadingDegrees.Value:F1} {CardinalDirection(f.HeadingDegrees.Value)}")
                : "[grey]--[/]";

            string vrate = f.VerticalRateMetersPerSecond.HasValue
                ? (f.VerticalRateMetersPerSecond.Value >= 0 ? "+" : "") +
                  f.VerticalRateMetersPerSecond.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "[grey]--[/]";

            table.AddRow(
                distance,
                $"[cyan]{Markup.Escape(f.Callsign)}[/]",
                Markup.Escape(f.Icao24),
                Markup.Escape(f.OriginCountry),
                FormatRoute(ef.Route),
                FormatAircraft(ef.Aircraft),
                FormatCategory(ef.Aircraft),
                altitude,
                speed,
                heading,
                FormatDirection(f.Latitude, f.Longitude, f.HeadingDegrees, f.DistanceKm, homeLat, homeLon),
                vrate,
                FormatEte(ef.Route, f.VelocityMetersPerSecond));
        }

        AnsiConsole.Write(table);
    }

    private static string FormatRoute(FlightRoute? route)
    {
        if (route is null)
            return "[grey]---[/]";

        // Show IATA codes (3-letter, most recognisable); fall back to ICAO then "???"
        string dep = route.OriginIata ?? route.OriginIcao ?? "???";
        string arr = route.DestIata   ?? route.DestIcao   ?? "???";

        return $"{Markup.Escape(dep)}[grey]→[/]{Markup.Escape(arr)}";
    }

    private static string FormatAircraft(AircraftInfo? info)
    {
        if (info is null)
            return "[grey]---[/]";

        var parts = new List<string>(2);
        if (info.TypeCode     is not null) parts.Add(Markup.Escape(info.TypeCode));
        if (info.Registration is not null) parts.Add(Markup.Escape(info.Registration));

        return parts.Count > 0
            ? string.Join(" [grey]/[/] ", parts)
            : "[grey]---[/]";
    }

    private static string FormatCategory(AircraftInfo? info)
    {
        if (info?.Category is null)
            return "[grey]---[/]";

        // Colour-code by type for quick scanning
        return info.Category switch
        {
            "Helicopter"     => $"[yellow]{Markup.Escape(info.Category)}[/]",
            "Wide-body Jet"  => $"[bold blue]{Markup.Escape(info.Category)}[/]",
            "Narrow-body Jet"=> $"[blue]{Markup.Escape(info.Category)}[/]",
            "Regional Jet"   => $"[aqua]{Markup.Escape(info.Category)}[/]",
            "Turboprop"      => $"[green]{Markup.Escape(info.Category)}[/]",
            "Light Aircraft" => $"[lime]{Markup.Escape(info.Category)}[/]",
            "Military"       => $"[red]{Markup.Escape(info.Category)}[/]",
            _                => Markup.Escape(info.Category)
        };
    }

    /// <summary>
    /// Estimates total flight time from route great-circle distance and current ground speed.
    /// This is an approximation: straight-line distance, not the actual filed route.
    /// </summary>
    private static string FormatEte(FlightRoute? route, double? speedMs)
    {
        if (route?.RouteDistanceKm is not double distKm || speedMs is not double speed || speed <= 0)
            return "[grey]---[/]";

        double hours = distKm / (speed * 3.6);
        int h = (int)hours;
        int m = (int)((hours - h) * 60);
        return h > 0
            ? $"{h}h {m:D2}m"
            : $"{m}m";
    }

    /// <summary>
    /// Classifies the flight's direction relative to home using the angular difference
    /// between the flight's heading and the bearing from the flight to home.
    /// </summary>
    private static string FormatDirection(
        double? lat, double? lon, double? heading, double? distKm,
        double homeLat, double homeLon)
    {
        // Within 5 km — essentially directly overhead regardless of heading
        if (distKm is <= 5.0)
            return "[bold white]⊙ Overhead[/]";

        if (lat is null || lon is null || heading is null)
            return "[grey]---[/]";

        // Bearing from flight position → home (degrees clockwise from north)
        double dLon  = ToRad(homeLon - lon.Value);
        double fLat  = ToRad(lat.Value);
        double hLat  = ToRad(homeLat);
        double y     = Math.Sin(dLon) * Math.Cos(hLat);
        double x     = Math.Cos(fLat) * Math.Sin(hLat) - Math.Sin(fLat) * Math.Cos(hLat) * Math.Cos(dLon);
        double bearingToHome = (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;

        // Angular difference folded to [0°, 180°]
        double diff = Math.Abs((heading.Value - bearingToHome + 360.0) % 360.0);
        if (diff > 180.0) diff = 360.0 - diff;

        return diff switch
        {
            <= 30  => "[green]↓ Towards[/]",
            >= 150 => "[red]↑ Away[/]",
            _      => "[yellow]→ Crossing[/]"
        };
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    // Maps a bearing (0–360°) to an 8-point cardinal direction abbreviation
    private static string CardinalDirection(double degrees) => degrees switch
    {
        >= 337.5 or < 22.5   => "N",
        >= 22.5  and < 67.5  => "NE",
        >= 67.5  and < 112.5 => "E",
        >= 112.5 and < 157.5 => "SE",
        >= 157.5 and < 202.5 => "S",
        >= 202.5 and < 247.5 => "SW",
        >= 247.5 and < 292.5 => "W",
        >= 292.5 and < 337.5 => "NW",
        _ => ""
    };
}
