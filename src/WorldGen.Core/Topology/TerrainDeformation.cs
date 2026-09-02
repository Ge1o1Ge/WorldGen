namespace WorldGen.Core.Topology;

public sealed record TerrainDeformationResult(
    IReadOnlyDictionary<CellAddress, int> DeltaCentimeters,
    int RemovedCentimeters,
    int DepositedCentimeters,
    int RadiusCells,
    int RimCells)
{
    public int BalanceErrorCentimeters => DepositedCentimeters - RemovedCentimeters;
}

/// <summary>
/// Sparse, centimetre-quantized changes above the immutable generated terrain.
/// Keeping this layer separate means untouched chunks remain procedural and free.
/// </summary>
public sealed class TerrainDeformationState
{
    private readonly IWorldTopology topology;
    private readonly Dictionary<CellAddress, int> offsets = [];

    public TerrainDeformationState(IWorldTopology topology) => this.topology = topology;
    public uint Revision { get; private set; }
    public IReadOnlyDictionary<CellAddress, int> OffsetsCentimeters => offsets;
    public float Apply(CellAddress cell, float baseElevation) => baseElevation + offsets.GetValueOrDefault(cell) / 100f;

    public TerrainDeformationResult ApplyDeltas(IReadOnlyDictionary<CellAddress, int> deltaCentimeters)
    {
        ArgumentNullException.ThrowIfNull(deltaCentimeters);
        var applied = new Dictionary<CellAddress, int>();
        var removed = 0;
        var deposited = 0;
        foreach (var (cell, delta) in deltaCentimeters)
        {
            if (!topology.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(deltaCentimeters));
            if (delta == 0) continue;
            var next = offsets.GetValueOrDefault(cell) + delta;
            if (next == 0) offsets.Remove(cell); else offsets[cell] = next;
            applied[cell] = delta;
            if (delta < 0) removed -= delta; else deposited += delta;
        }
        if (applied.Count > 0) Revision++;
        return new(applied, removed, deposited, 0, 0);
    }

    public TerrainDeformationResult Impact(CellAddress center, int radiusCells, double depthMeters)
    {
        if (!topology.Contains(center)) throw new ArgumentOutOfRangeException(nameof(center));
        if (radiusCells is < 2 or > 64) throw new ArgumentOutOfRangeException(nameof(radiusCells));
        if (!double.IsFinite(depthMeters) || depthMeters is < .01 or > 5000) throw new ArgumentOutOfRangeException(nameof(depthMeters));

        var outerRadius = Math.Max(radiusCells + 1, (int)Math.Ceiling(radiusCells * 1.5));
        var centerPoint = topology.ToUnitVector(center);
        var cellAngle = topology.GetNeighbors(center)
            .Average(neighbor => Math.Acos(Math.Clamp(centerPoint.Dot(topology.ToUnitVector(neighbor)), -1, 1)));
        // Cardinal graph distance is diamond-shaped. Use it only to collect a
        // generous candidate set, then shape the crater by true angular distance
        // on the sphere so diagonals do not become square corners.
        var candidateHops = (int)Math.Ceiling(outerRadius * 1.75);
        var distance = new Dictionary<CellAddress, int> { [center] = 0 };
        var queue = new Queue<CellAddress>(); queue.Enqueue(center);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue(); var nextDistance = distance[cell] + 1;
            if (nextDistance > candidateHops) continue;
            foreach (var neighbor in topology.GetNeighbors(cell))
                if (distance.TryAdd(neighbor, nextDistance)) queue.Enqueue(neighbor);
        }

        var radial = distance.Keys.ToDictionary(cell => cell,
            cell => Math.Acos(Math.Clamp(centerPoint.Dot(topology.ToUnitVector(cell)), -1, 1)) / cellAngle);

        var eventDelta = new Dictionary<CellAddress, int>();
        var removed = 0;
        foreach (var (cell, d) in radial.Where(pair => pair.Value <= radiusCells))
        {
            var t = d / (double)radiusCells;
            var centimeters = Math.Max(1, (int)Math.Round(depthMeters * 100 * Math.Pow(1 - t * t, 2)));
            eventDelta[cell] = -centimeters; removed += centimeters;
        }

        var rim = radial.Where(pair => pair.Value > radiusCells && pair.Value <= outerRadius)
            .Select(pair => (pair.Key, Weight: Math.Pow(1 - (pair.Value - radiusCells) / Math.Max(1, outerRadius - radiusCells), 2)))
            .Where(pair => pair.Weight > 0).ToArray();
        var weightSum = rim.Sum(pair => pair.Weight); var deposited = 0;
        foreach (var (cell, weight) in rim)
        {
            var centimeters = Math.Max(0, (int)Math.Floor(removed * weight / weightSum));
            if (centimeters == 0) continue; eventDelta[cell] = centimeters; deposited += centimeters;
        }
        // Integer-centimetre storage still conserves the excavated column sum.
        for (var index = 0; deposited < removed && rim.Length > 0; index = (index + 1) % rim.Length)
        { eventDelta[rim[index].Key] = eventDelta.GetValueOrDefault(rim[index].Key) + 1; deposited++; }

        foreach (var (cell, delta) in eventDelta)
        {
            var next = offsets.GetValueOrDefault(cell) + delta;
            if (next == 0) offsets.Remove(cell); else offsets[cell] = next;
        }
        Revision++;
        return new(eventDelta, removed, deposited, radiusCells, rim.Length);
    }
}
