namespace FlightTracker.Display;

using System.Globalization;
using FlightTracker.Models;
using Spectre.Console;

public static class FlightTableRenderer
{
    public static void Render(
        IReadOnlyList<FlightState> flights,
        double homeLat,
        double homeLon,
        DateTimeOffset timestamp)
    {
        AnsiConsole.Clear();

        AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"[bold cyan]Flight Tracker[/]  " +
            $"Home: [yellow]{homeLat:F4}, {homeLon:F4}[/]  " +
            $"Last poll: [green]{timestamp:HH:mm:ss}[/]  " +
            $"Overhead: [white]{flights.Count}[/]"));

        AnsiConsole.WriteLine();

        if (flights.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No flights detected in bounding box.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Callsign[/]").Centered())
            .AddColumn(new TableColumn("[bold]ICAO24[/]").Centered())
            .AddColumn(new TableColumn("[bold]Country[/]"))
            .AddColumn(new TableColumn("[bold]Alt (m)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Speed (m/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Heading[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]V/Rate (m/s)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]On Ground[/]").Centered());

        foreach (var f in flights.OrderBy(f => f.Callsign))
        {
            string onGround = f.OnGround ? "[yellow]Yes[/]" : "[green]No[/]";

            string altitude = f.BarometricAltitudeMeters.HasValue
                ? f.BarometricAltitudeMeters.Value.ToString("F0", CultureInfo.InvariantCulture)
                : "[grey]--[/]";

            string speed = f.VelocityMetersPerSecond.HasValue
                ? f.VelocityMetersPerSecond.Value.ToString("F1", CultureInfo.InvariantCulture)
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
                $"[cyan]{Markup.Escape(f.Callsign)}[/]",
                Markup.Escape(f.Icao24),
                Markup.Escape(f.OriginCountry),
                altitude,
                speed,
                heading,
                vrate,
                onGround);
        }

        AnsiConsole.Write(table);
    }

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
