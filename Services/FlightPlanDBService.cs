namespace FlightTracker.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Retrieves filed flight plans from FlightPlanDatabase.com.
/// Picks the most popular plan with ≥ 6 waypoints for a given route pair.
/// Results are cached by (fromIcao, toIcao) for the lifetime of the service.
/// Free tier: 100 requests/day by IP.
/// </summary>
public sealed class FlightPlanDBService : IFlightPlanDBService
{
    // Minimum waypoints to consider a plan useful (filters out trivial 3-node direct plans)
    private const int MinUsefulWaypoints = 6;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, FlightPlanResult?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FlightPlanDBService(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient("flightplandatabase");
    }

    public async Task<FlightPlanResult?> GetRouteAsync(
        string fromIcao, string toIcao,
        CancellationToken ct = default)
    {
        string key = $"{fromIcao.ToUpperInvariant()}:{toIcao.ToUpperInvariant()}";

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await FetchAsync(fromIcao, toIcao, ct);
        _cache[key] = result;
        return result;
    }

    private async Task<FlightPlanResult?> FetchAsync(
        string fromIcao, string toIcao, CancellationToken ct)
    {
        try
        {
            // Step 1: search for plans, sorted by popularity
            string searchUrl = $"search/plans?fromICAO={Uri.EscapeDataString(fromIcao)}" +
                               $"&toICAO={Uri.EscapeDataString(toIcao)}&limit=10";

            using var searchResp = await _http.GetAsync(searchUrl, ct);
            if (!searchResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[FlightPlanDB] Search {fromIcao}→{toIcao}: HTTP {(int)searchResp.StatusCode}");
                return null;
            }

            await using var searchStream = await searchResp.Content.ReadAsStreamAsync(ct);
            var stubs = await JsonSerializer.DeserializeAsync<List<FpdbPlanStub>>(searchStream, JsonOptions, ct);
            if (stubs is null || stubs.Count == 0)
            {
                Console.WriteLine($"[FlightPlanDB] {fromIcao}→{toIcao}: no plans found");
                return null;
            }

            // Pick the plan with the most waypoints (≥ MinUsefulWaypoints), tie-break by popularity
            var best = stubs
                .Where(p => p.Waypoints >= MinUsefulWaypoints)
                .OrderByDescending(p => p.Waypoints)
                .ThenByDescending(p => p.Popularity)
                .FirstOrDefault();

            if (best is null)
            {
                Console.WriteLine($"[FlightPlanDB] {fromIcao}→{toIcao}: all {stubs.Count} plans have < {MinUsefulWaypoints} waypoints (trivial direct)");
                return null;
            }

            // Step 2: fetch the full plan with route nodes
            using var planResp = await _http.GetAsync($"plan/{best.Id}", ct);
            if (!planResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[FlightPlanDB] Fetch plan {best.Id}: HTTP {(int)planResp.StatusCode}");
                return null;
            }

            await using var planStream = await planResp.Content.ReadAsStreamAsync(ct);
            var plan = await JsonSerializer.DeserializeAsync<FpdbPlan>(planStream, JsonOptions, ct);
            if (plan?.Route?.Nodes is null || plan.Route.Nodes.Count == 0)
            {
                Console.WriteLine($"[FlightPlanDB] Plan {best.Id}: empty route");
                return null;
            }

            // Extract ordered points and airway names
            var points = new List<(double Lat, double Lon)>(plan.Route.Nodes.Count);
            var airways = new List<string>();

            foreach (var node in plan.Route.Nodes)
            {
                points.Add((node.Lat, node.Lon));
                if (node.Via?.Ident is { Length: > 0 } awy && !airways.Contains(awy))
                    airways.Add(awy);
            }

            Console.WriteLine($"[FlightPlanDB] {fromIcao}→{toIcao}: plan {best.Id} · {points.Count} wpts via {string.Join("→", airways)}");
            return new FlightPlanResult(points.AsReadOnly(), best.Id, airways.AsReadOnly());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[FlightPlanDB] {fromIcao}→{toIcao}: {ex.Message}");
            return null;
        }
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class FpdbPlanStub
    {
        public int Id { get; set; }
        public int Waypoints { get; set; }
        public long Popularity { get; set; }
    }

    private sealed class FpdbPlan
    {
        public FpdbRoute? Route { get; set; }
    }

    private sealed class FpdbRoute
    {
        public List<FpdbNode>? Nodes { get; set; }
    }

    private sealed class FpdbNode
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public FpdbVia? Via { get; set; }
    }

    private sealed class FpdbVia
    {
        public string? Ident { get; set; }
    }
}
