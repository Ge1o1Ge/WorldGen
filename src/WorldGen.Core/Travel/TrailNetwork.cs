using WorldGen.Core.Topology;

namespace WorldGen.Core.Travel;

public sealed record PathSearchResult(IReadOnlyList<CellAddress> Cells, float TotalCost, int VisitedCells);

public static class CellPathfinder
{
    public static PathSearchResult? FindPath(
        IWorldTopology topology,
        CellAddress start,
        CellAddress destination,
        Func<CellAddress, float> traversalCost,
        int maximumVisitedCells = 100_000)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(traversalCost);
        if (!topology.Contains(start) || !topology.Contains(destination))
            throw new ArgumentOutOfRangeException(nameof(start));
        if (maximumVisitedCells < 1) throw new ArgumentOutOfRangeException(nameof(maximumVisitedCells));
        if (start == destination) return new PathSearchResult([start], 0, 1);

        var frontier = new PriorityQueue<CellAddress, (float Cost, long Sequence)>();
        var costs = new Dictionary<CellAddress, float> { [start] = 0 };
        var previous = new Dictionary<CellAddress, CellAddress>();
        long sequence = 0;
        frontier.Enqueue(start, (0, sequence++));
        var visited = 0;

        while (frontier.TryDequeue(out var current, out var queued))
        {
            if (!costs.TryGetValue(current, out var currentCost) || queued.Cost > currentCost + 0.00001f)
                continue;
            visited++;
            if (current == destination) return Reconstruct(destination, currentCost, visited, previous);
            if (visited >= maximumVisitedCells) return null;

            foreach (var neighbor in topology.GetNeighbors(current))
            {
                var stepCost = traversalCost(neighbor);
                if (!float.IsFinite(stepCost) || stepCost <= 0) continue;
                var nextCost = currentCost + stepCost;
                if (costs.TryGetValue(neighbor, out var known) && known <= nextCost) continue;
                costs[neighbor] = nextCost;
                previous[neighbor] = current;
                frontier.Enqueue(neighbor, (nextCost, sequence++));
            }
        }

        return null;
    }

    private static PathSearchResult Reconstruct(
        CellAddress destination,
        float totalCost,
        int visited,
        IReadOnlyDictionary<CellAddress, CellAddress> previous)
    {
        var path = new List<CellAddress> { destination };
        var current = destination;
        while (previous.TryGetValue(current, out var parent))
        {
            path.Add(parent);
            current = parent;
        }
        path.Reverse();
        return new PathSearchResult(path, totalCost, visited);
    }
}

/// <summary>
/// Sparse mutable delta over immutable terrain. Traffic compacts a trail asymptotically;
/// disused trails fade instead of remaining permanent map content.
/// </summary>
public sealed class TrailField
{
    private readonly Dictionary<CellAddress, float> strengths = new();

    public TrailField(float trafficForStrongTrail = 240, float halfLifeDays = 720, float maximumCostReduction = 0.68f)
    {
        if (trafficForStrongTrail <= 0) throw new ArgumentOutOfRangeException(nameof(trafficForStrongTrail));
        if (halfLifeDays <= 0) throw new ArgumentOutOfRangeException(nameof(halfLifeDays));
        if (maximumCostReduction is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(maximumCostReduction));
        TrafficForStrongTrail = trafficForStrongTrail;
        HalfLifeDays = halfLifeDays;
        MaximumCostReduction = maximumCostReduction;
    }

    public float TrafficForStrongTrail { get; }
    public float HalfLifeDays { get; }
    public float MaximumCostReduction { get; }
    public IReadOnlyDictionary<CellAddress, float> Strengths => strengths;

    public float GetStrength(CellAddress cell) => strengths.GetValueOrDefault(cell);

    public float EffectiveTraversalCost(CellAddress cell, float baseCost)
    {
        if (!float.IsFinite(baseCost) || baseCost <= 0) return baseCost;
        return baseCost * (1 - GetStrength(cell) * MaximumCostReduction);
    }

    public void RecordPassage(IEnumerable<CellAddress> path, float travelers)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!float.IsFinite(travelers) || travelers <= 0) throw new ArgumentOutOfRangeException(nameof(travelers));
        var gain = 1 - MathF.Exp(-travelers / TrafficForStrongTrail);
        foreach (var cell in path.Distinct())
        {
            var existing = GetStrength(cell);
            strengths[cell] = Math.Clamp(existing + (1 - existing) * gain, 0, 1);
        }
    }

    public void Decay(float days)
    {
        if (!float.IsFinite(days) || days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        if (days == 0 || strengths.Count == 0) return;
        var factor = MathF.Pow(0.5f, days / HalfLifeDays);
        foreach (var cell in strengths.Keys.ToArray())
        {
            var strength = strengths[cell] * factor;
            if (strength < 0.001f) strengths.Remove(cell);
            else strengths[cell] = strength;
        }
    }
}
