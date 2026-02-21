namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using FlightTracker.Models;

public sealed class HexDbService : IAircraftInfoService
{
    private readonly HttpClient _httpClient;

    // Session-lifetime cache: registration/type never changes mid-session.
    // Stores AircraftInfo? — null means "fetched but API had no record".
    private readonly ConcurrentDictionary<string, AircraftInfo?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Coalesces concurrent first-poll calls for the same ICAO24 into one HTTP request.
    private readonly ConcurrentDictionary<string, Task<AircraftInfo?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HexDbService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("hexdb");
        _httpClient.BaseAddress = new Uri("https://hexdb.io/api/v1/aircraft/");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public Task<AircraftInfo?> GetAircraftInfoAsync(string icao24, CancellationToken cancellationToken)
    {
        // Hot path: already in cache (null result also cached — no retry for unknown aircraft)
        if (_cache.TryGetValue(icao24, out var cached))
            return Task.FromResult(cached);

        // Coalesce concurrent callers for the same ICAO24 onto a single Task
        return _inFlight.GetOrAdd(icao24, key => FetchAndCacheAsync(key, cancellationToken));
    }

    private async Task<AircraftInfo?> FetchAndCacheAsync(string icao24, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(icao24, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _cache[icao24] = null;
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var dto = await JsonSerializer.DeserializeAsync<HexDbAircraftResponse>(
                stream, JsonOptions, cancellationToken);

            // Treat empty type codes as null (hexdb returns "" for unknown aircraft)
            string? typeCode = NullIfEmpty(dto?.ICAOTypeCode);
            string? reg      = NullIfEmpty(dto?.Registration);
            string? owner    = NullIfEmpty(dto?.RegisteredOwners);

            AircraftInfo? info = (typeCode is null && reg is null && owner is null)
                ? null
                : new AircraftInfo(typeCode, reg, owner, DeriveCategory(typeCode));

            _cache[icao24] = info;
            return info;
        }
        catch
        {
            // On any network error, cache null to avoid flooding on every poll
            _cache[icao24] = null;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(icao24, out _);
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Maps an ICAO type code to a friendly human-readable category.
    /// Returns null when the type code is unknown or empty.
    /// </summary>
    private static string? DeriveCategory(string? typeCode)
    {
        if (string.IsNullOrEmpty(typeCode))
            return null;

        string t = typeCode.ToUpperInvariant();

        // ── Helicopters ─────────────────────────────────────────────────────────
        if (IsHelicopter(t)) return "Helicopter";

        // ── Wide-body jets ──────────────────────────────────────────────────────
        if (IsWidebody(t)) return "Wide-body Jet";

        // ── Narrow-body jets ────────────────────────────────────────────────────
        if (IsNarrowbody(t)) return "Narrow-body Jet";

        // ── Regional jets ───────────────────────────────────────────────────────
        if (IsRegionalJet(t)) return "Regional Jet";

        // ── Turboprops ──────────────────────────────────────────────────────────
        if (IsTurboprop(t)) return "Turboprop";

        // ── Military / special ──────────────────────────────────────────────────
        if (IsMilitary(t)) return "Military";

        // ── Light / general aviation ────────────────────────────────────────────
        if (IsLightAircraft(t)) return "Light Aircraft";

        return null; // unknown type — don't guess
    }

    private static bool IsHelicopter(string t) =>
        // Eurocopter / Airbus Helicopters
        t.StartsWith("EC") || t.StartsWith("AS3") || t.StartsWith("AS3") ||
        t is "H120" or "H125" or "H130" or "H135" or "H145" or "H155" or "H160" or "H175" or "H215" or "H225" ||
        // Robinson
        t is "R22" or "R44" or "R66" ||
        // Bell
        t.StartsWith("B06") || t.StartsWith("B21") || t.StartsWith("B42") || t.StartsWith("B43") ||
        t is "B412" or "B427" or "B429" or "B430" or "B505" ||
        // Leonardo / AgustaWestland
        t is "A109" or "A119" or "A139" or "A149" or "A169" or "A189" ||
        // Sikorsky
        t.StartsWith("S61") || t.StartsWith("S76") || t.StartsWith("S92") ||
        t is "S300" or "S333" ||
        // MD helicopters
        t is "MD52" or "MD60" or "MD90H" ||
        // Generic ICAO H-prefix (e.g. H500, H269)
        (t.Length >= 2 && t[0] == 'H' && char.IsDigit(t[1]));

    private static bool IsWidebody(string t) =>
        // Boeing wide-bodies
        t is "B741" or "B742" or "B743" or "B744" or "B748" or "B74S" or "B74D" ||
        t is "B762" or "B763" or "B764" or "B772" or "B773" or "B778" or "B779" or "B77L" or "B77W" ||
        t is "B788" or "B789" or "B78X" ||
        // Airbus wide-bodies
        t is "A306" or "A30B" or "A310" ||
        t.StartsWith("A330") || t.StartsWith("A332") || t.StartsWith("A333") || t.StartsWith("A338") || t.StartsWith("A339") ||
        t.StartsWith("A340") || t.StartsWith("A342") || t.StartsWith("A343") || t.StartsWith("A345") || t.StartsWith("A346") ||
        t.StartsWith("A350") || t.StartsWith("A358") || t.StartsWith("A359") || t.StartsWith("A35K") ||
        t.StartsWith("A380") || t.StartsWith("A388") ||
        t.StartsWith("A390") ||
        // McDonnell Douglas / other
        t is "DC10" or "MD11" or "L101" or "C5" or "AN22" or "AN124" or "AN225";

    private static bool IsNarrowbody(string t) =>
        // Airbus A220 family (ex-Bombardier C Series)
        t is "BCS1" or "BCS3" or "A220" or "A221" or "A223" ||
        // Airbus A320 family
        t.StartsWith("A318") || t.StartsWith("A319") || t.StartsWith("A320") || t.StartsWith("A321") ||
        t is "A318" or "A319" or "A320" or "A321" ||
        // Boeing 737 family
        t.StartsWith("B73") || t is "B731" or "B732" or "B733" or "B734" or "B735" or "B736" or "B737" or "B738" or "B739" or "B73G" or "B73H" ||
        // Boeing 757
        t is "B752" or "B753" ||
        // MD80/90
        t is "MD81" or "MD82" or "MD83" or "MD87" or "MD88" or "MD90" ||
        // 717
        t is "B712";

    private static bool IsRegionalJet(string t) =>
        // Embraer E-jets
        t.StartsWith("E13") || t.StartsWith("E14") || t.StartsWith("E17") || t.StartsWith("E19") ||
        t is "E135" or "E145" or "E170" or "E175" or "E190" or "E195" or "E290" or "E295" ||
        // Bombardier CRJ
        t.StartsWith("CRJ") ||
        t is "CRJ1" or "CRJ2" or "CRJ7" or "CRJ9" or "CRJX" ||
        // Sukhoi Superjet
        t is "SU95" ||
        // BAe 146 / Avro RJ
        t is "RJ1H" or "RJ70" or "RJ85" or "RJ1" or "BA46" or "B461" or "B462" or "B463" ||
        // Fokker
        t is "F28" or "F70" or "F100" ||
        // COMAC ARJ21
        t is "ARJ1";

    private static bool IsTurboprop(string t) =>
        // ATR
        t is "AT43" or "AT45" or "AT46" or "AT72" or "AT73" or "AT75" or "AT76" ||
        // Bombardier Dash 8 / Q-series
        t is "DH8A" or "DH8B" or "DH8C" or "DH8D" or "DHC8" or "DH84" ||
        // Saab
        t is "SF34" or "S340" or "S2000" ||
        // Beechcraft / Raytheon King Air family
        t is "BE20" or "BE30" or "BE60" or "B350" or "B190" or "B1900" ||
        // Pilatus
        t is "PC12" or "PC24" ||
        // Cessna turboprops
        t is "C208" or "C210" or "C441" ||
        // de Havilland Canada
        t is "DHC6" or "DHC7" or "DHC2" ||
        // Dornier
        t is "DO28" or "DO228" or "D228" ||
        // Let / Antonov smaller turboprops
        t is "L410" or "AN26" or "AN28" or "AN32";

    private static bool IsMilitary(string t) =>
        t is "F15" or "F16" or "F18" or "F22" or "F35" or "F117" or
            "B1" or "B2" or "B52" or
            "C130" or "C17" or "C5" or
            "E3" or "E8" or
            "KC10" or "KC135" or
            "P3" or "P8" or
            "U2" or "SR71" or "A10" or
            "EUFI" or "TPHN" or "MRTT";

    private static bool IsLightAircraft(string t) =>
        // Cessna piston singles
        t.StartsWith("C1") || t.StartsWith("C17") ||
        t is "C150" or "C152" or "C162" or "C172" or "C177" or "C182" or "C185" or "C206" or "C207" ||
        // Cessna twins
        t is "C310" or "C340" or "C402" or "C404" or "C421" ||
        // Piper
        t is "PA18" or "PA28" or "PA32" or "PA34" or "PA38" or "PA44" or "PA46" ||
        // Beechcraft piston
        t is "BE33" or "BE35" or "BE36" or "BE55" or "BE58" or "BE76" ||
        // Cirrus
        t is "SR20" or "SR22" ||
        // Diamond
        t is "DA20" or "DA40" or "DA42" or "DA50" or "DA62" ||
        // Robin / Tecnam / Aquila / light sport
        t.StartsWith("DR4") || t is "P2002" or "P2006" or "P2010" or "AQUI" ||
        // Socata / TBM (piston versions)
        t is "TB9" or "TB10" or "TB20" or "TB21";
}
