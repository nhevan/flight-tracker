namespace FlightTracker.Services;

using System.Text.RegularExpressions;
using FlightTracker.Models;

/// <summary>
/// Parses the open-flightmaps ARINC 424 .pc files at startup and exposes a
/// fast fix-name → lat/lon lookup.
///
/// Supported ARINC 424 sections:
///   D   — VOR/NDB navaids         (col 5 = 'D', col 6 = ' ' or 'B')
///   EA  — Enroute waypoints       (col 5 = 'E', col 6 = 'A')
///   PC  — Terminal area fixes     (col 5 = 'P', col 6 = 'C')
///
/// Lat/lon are extracted via a regex that matches the ARINC DMS encoding:
///   N|S DDMMSS.SS  E|W DDDMMSS.SS
/// (encoded as 8 and 9 digit strings, tenths-of-seconds, no decimal point in file)
/// </summary>
public sealed class Arinc424NavDataService : IArinc424NavDataService
{
    // Maps fix name (upper) → one or more NavFix entries (same name can appear in
    // different terminal areas or as both a waypoint and a navaid).
    private readonly Dictionary<string, List<NavFix>> _fixes =
        new(StringComparer.OrdinalIgnoreCase);

    // Matches a lat/lon pair anywhere on a record line.
    // Group 1: N/S, groups 2-4: degrees, minutes, seconds (tenths)
    // Group 5: E/W, groups 6-8: degrees, minutes, seconds (tenths)
    private static readonly Regex LatLonRe = new(
        @"([NS])(\d{2})(\d{2})(\d{4})[EW]?([EW])(\d{3})(\d{2})(\d{4})",
        RegexOptions.Compiled);

    public Arinc424NavDataService(string dataDirectory)
    {
        LoadDirectory(dataDirectory);
        Console.WriteLine(
            $"[Arinc424] Loaded {_fixes.Count} unique fix names " +
            $"({_fixes.Values.Sum(l => l.Count)} total entries).");
    }

    public NavFix? TryResolveFix(string name, double hintLat = 52.0, double hintLon = 4.5)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (!_fixes.TryGetValue(name.Trim().ToUpperInvariant(), out var candidates))
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        // When multiple fixes share a name, return the one closest to the hint position.
        return candidates
            .OrderBy(f => DistanceSq(f.Lat, f.Lon, hintLat, hintLon))
            .First();
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private void LoadDirectory(string dir)
    {
        // Load both embedded and isolated sub-directories
        foreach (string subDir in new[] { Path.Combine(dir, "embedded"), Path.Combine(dir, "isolated") })
        {
            if (!Directory.Exists(subDir))
                continue;

            foreach (string file in Directory.GetFiles(subDir, "*.pc"))
                LoadFile(file);
        }
    }

    private void LoadFile(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                if (line.Length < 30) continue;

                // Col 5 = section code, col 6 = subsection code (1-indexed)
                char section    = line[4];
                char subsection = line[5];

                NavFixType? type = (section, subsection) switch
                {
                    ('D', ' ') => NavFixType.Vor,
                    ('D', 'B') => NavFixType.Ndb,
                    ('E', 'A') => NavFixType.Waypoint,
                    ('P', 'C') => NavFixType.Waypoint,
                    _          => null
                };

                if (type is null) continue;

                string? name    = ExtractFixName(line, section);
                (double lat, double lon)? coords = ExtractLatLon(line);

                if (name is not null && coords.HasValue)
                {
                    var fix = new NavFix(name, coords.Value.lat, coords.Value.lon, type.Value);
                    if (!_fixes.TryGetValue(name, out var list))
                        _fixes[name] = list = [];
                    list.Add(fix);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Arinc424] Warning: failed to load {filePath}: {ex.Message}");
        }
    }

    // ── Field extraction ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the fix identifier from the appropriate column(s).
    ///
    /// ARINC 424 fixed-width layout (1-indexed):
    ///   D section:  col 14–18 (5 chars) — primary identifier; col 33–37 is a repeat
    ///   PC section: col 14–18 (5 chars)
    ///   EA section: col 14–18 (5 chars)
    /// Col indices here are 0-based (14 → index 13).
    /// </summary>
    private static string? ExtractFixName(string line, char section)
    {
        // All three section types put the identifier at cols 14–18 (0-based: 13–17)
        if (line.Length < 18) return null;

        string raw = line.Substring(13, 5).Trim();
        return string.IsNullOrEmpty(raw) ? null : raw.ToUpperInvariant();
    }

    /// <summary>
    /// Extracts the first lat/lon pair from the record line using the ARINC DMS regex.
    /// ARINC encodes tenths-of-seconds (no decimal point), e.g. N52195751 = N 52°19'57.51
    /// which decodes to 52 + 19/60 + 57.51/3600 = 52.332642° N.
    /// </summary>
    private static (double lat, double lon)? ExtractLatLon(string line)
    {
        Match m = LatLonRe.Match(line);
        if (!m.Success) return null;

        double lat = ParseDms(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        double lon = ParseDms(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);

        return (lat, lon);
    }

    private static double ParseDms(string hemi, string deg, string min, string sec100)
    {
        // sec100 is seconds × 100, e.g. "5751" = 57.51 seconds
        double d  = double.Parse(deg);
        double m  = double.Parse(min);
        double s  = double.Parse(sec100) / 100.0;
        double dd = d + m / 60.0 + s / 3600.0;
        return (hemi is "S" or "W") ? -dd : dd;
    }

    private static double DistanceSq(double lat1, double lon1, double lat2, double lon2) =>
        (lat1 - lat2) * (lat1 - lat2) + (lon1 - lon2) * (lon1 - lon2);
}
