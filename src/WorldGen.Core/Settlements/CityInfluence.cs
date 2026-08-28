using WorldGen.Core.Topology;

namespace WorldGen.Core.Settlements;

public enum CityAssetKind : byte
{
    Building,
    CultivatedField,
    Pasture,
    Orchard,
    Infrastructure
}

/// <summary>
/// A real occupied or used place, not an abstract city-center radius. Removing the asset
/// removes its claim source on the next influence rebuild.
/// </summary>
public sealed record CityInfluenceSource(
    string Id,
    string CityId,
    CellAddress Cell,
    CityAssetKind Kind,
    float Strength);

public sealed record CityInfluenceSettings(float FalloffPerTravelCost, float MinimumClaim)
{
    public static CityInfluenceSettings Default { get; } = new(0.08f, 0.05f);
}

public sealed record CityInfluenceCell(string CityId, float Strength, bool IsBoundary);

public sealed class CityInfluenceMap
{
    internal CityInfluenceMap(Dictionary<CellAddress, CityInfluenceCell> cells) => Cells = cells;

    public IReadOnlyDictionary<CellAddress, CityInfluenceCell> Cells { get; }

    public IEnumerable<CellAddress> BoundaryCells => Cells
        .Where(pair => pair.Value.IsBoundary)
        .Select(pair => pair.Key);
}

public static class CityInfluenceEngine
{
    public static CityInfluenceMap Build(
        IWorldTopology topology,
        IEnumerable<CityInfluenceSource> source,
        Func<CellAddress, float> traversalCost,
        CityInfluenceSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(traversalCost);
        settings ??= CityInfluenceSettings.Default;
        if (settings.FalloffPerTravelCost <= 0 || settings.MinimumClaim <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings));

        var sources = source.OrderBy(item => item.CityId, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (sources.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.CityId)))
            throw new ArgumentException("Источник влияния должен иметь идентификаторы", nameof(source));
        if (sources.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new ArgumentException("Идентификаторы источников влияния должны быть уникальны", nameof(source));
        if (sources.Any(item => !topology.Contains(item.Cell) || item.Strength < settings.MinimumClaim))
            throw new ArgumentException("Источник влияния находится вне мира или слишком слаб", nameof(source));

        var claims = new Dictionary<ClaimKey, float>();
        var queue = new PriorityQueue<ClaimKey, float>();
        foreach (var item in sources)
        {
            var key = new ClaimKey(item.CityId, item.Cell);
            if (claims.TryGetValue(key, out var existing) && existing >= item.Strength) continue;
            claims[key] = item.Strength;
            queue.Enqueue(key, -item.Strength);
        }

        while (queue.TryDequeue(out var current, out var priority))
        {
            var queuedStrength = -priority;
            if (!claims.TryGetValue(current, out var currentStrength) || queuedStrength < currentStrength - 0.00001f)
                continue;

            foreach (var neighbor in topology.GetNeighbors(current.Cell))
            {
                var cost = traversalCost(neighbor);
                if (!float.IsFinite(cost) || cost <= 0) continue;
                var nextStrength = currentStrength - settings.FalloffPerTravelCost * cost;
                if (nextStrength < settings.MinimumClaim) continue;
                var next = new ClaimKey(current.CityId, neighbor);
                if (claims.TryGetValue(next, out var known) && known >= nextStrength) continue;
                claims[next] = nextStrength;
                queue.Enqueue(next, -nextStrength);
            }
        }

        var winners = claims
            .GroupBy(pair => pair.Key.Cell)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => new Candidate(pair.Key.CityId, pair.Value))
                    .OrderByDescending(candidate => candidate.Strength)
                    .ThenBy(candidate => candidate.CityId, StringComparer.Ordinal)
                    .First());
        var cells = new Dictionary<CellAddress, CityInfluenceCell>(winners.Count);
        foreach (var (cell, winner) in winners)
        {
            var boundary = topology.GetNeighbors(cell).Any(neighbor =>
                !winners.TryGetValue(neighbor, out var other) ||
                !string.Equals(winner.CityId, other.CityId, StringComparison.Ordinal));
            cells[cell] = new CityInfluenceCell(winner.CityId, winner.Strength, boundary);
        }

        return new CityInfluenceMap(cells);
    }

    private readonly record struct ClaimKey(string CityId, CellAddress Cell);
    private readonly record struct Candidate(string CityId, float Strength);
}
