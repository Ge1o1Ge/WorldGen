namespace WorldGen.Core.Topology;

/// <summary>
/// Cached drainage substrate at simulation-cell precision. Priority flooding connects depressions to their
/// spill elevation; flow is accumulated in reverse flood order, so cycles are impossible.
/// This is potential runoff, not a seasonal water-volume or erosion simulation.
/// </summary>
public sealed class SphericalHydrology
{
    public const string GeneratorVersion = "priority-flood-v2";
    public const float LakeDepthThreshold = 1;
    public const float MinimumRiverRunoff = 55;

    private SphericalHydrology(int resolution, float seaLevel, float[] elevation, float[] moisture, float runoffWeight)
    {
        Topology = new CubeSphereTopology(resolution);
        SeaLevel = seaLevel;
        Elevation = elevation;
        Surface = new float[elevation.Length];
        Downstream = Enumerable.Repeat(-1, elevation.Length).ToArray();
        Runoff = new float[elevation.Length];
        RunoffWeight = runoffWeight;
        var visited = new bool[elevation.Length];
        var order = new int[elevation.Length];
        var queue = new PriorityQueue<int, (float Height, int Id)>();
        for (var i = 0; i < elevation.Length; i++)
        {
            Runoff[i] = elevation[i] > seaLevel ? Math.Max(0.05f, moisture[i]) * runoffWeight : 0;
            if (elevation[i] <= seaLevel) Seed(i, seaLevel);
        }
        // An all-land test world is an endorheic basin, never an invented edge outlet.
        if (queue.Count == 0)
        {
            var minimum = Array.IndexOf(elevation, elevation.Min());
            Seed(minimum, elevation[minimum]);
        }

        // Interior cells need no HashSet, sorting, validation or spherical projection.
        // Seam cells retain the exact deterministic neighbor order of the topology.
        Span<int> neighbors = stackalloc int[8];
        var count = 0;
        while (queue.TryDequeue(out var current, out _))
        {
            order[count++] = current;
            var neighborCount = NeighborIndices(current, neighbors);
            for (var n = 0; n < neighborCount; n++)
            {
                var neighbor = neighbors[n];
                if (visited[neighbor]) continue;
                visited[neighbor] = true;
                Downstream[neighbor] = current;
                Surface[neighbor] = Math.Max(elevation[neighbor], Surface[current]);
                queue.Enqueue(neighbor, (Surface[neighbor], neighbor));
            }
        }
        // On slopes choose the steepest available descent, not whichever queue edge
        // first discovered a cell. Flood order is used only to drain equal-height flats.
        for (var current = 0; current < elevation.Length; current++)
        {
            if (Downstream[current] < 0) continue;
            var point = Topology.ToUnitVector(Address(current));
            var bestSlope = 0d;
            var neighborCount = NeighborIndices(current, neighbors);
            for (var n = 0; n < neighborCount; n++)
            {
                var next = neighbors[n];
                var drop = Surface[current] - Surface[next];
                if (drop <= 0) continue;
                var distance = Math.Acos(Math.Clamp(point.Dot(Topology.ToUnitVector(Address(next))), -1, 1));
                var slope = drop / Math.Max(1e-9, distance);
                if (slope <= bestSlope) continue;
                bestSlope = slope;
                Downstream[current] = next;
            }
        }
        // Lower surfaces are earlier in the flood order; flats retain their original
        // earlier parent. Re-sorting is unnecessary, and accumulated runoff is conserved.
        for (var i = count - 1; i >= 0; i--)
        {
            var current = order[i];
            if (Downstream[current] >= 0) Runoff[Downstream[current]] += Runoff[current];
        }
        LakeShore = BuildLakeShore();

        void Seed(int index, float surface)
        {
            visited[index] = true;
            Surface[index] = surface;
            queue.Enqueue(index, (surface, index));
        }
    }

    public CubeSphereTopology Topology { get; }
    public int Resolution => Topology.FaceSize;
    public float SeaLevel { get; }
    public float[] Elevation { get; }
    public float[] Surface { get; }
    public int[] Downstream { get; }
    public float[] Runoff { get; }
    public float RunoffWeight { get; }
    /// <summary>Signed lake depth relative to the water threshold, extended onto the
    /// dry shore using the neighboring basin's level, never by zero-clamping depths.</summary>
    public float[] LakeShore { get; }

    public bool IsLake(int index) => Elevation[index] > SeaLevel && Surface[index] - Elevation[index] > LakeDepthThreshold;
    public bool IsWater(int index) => Elevation[index] <= SeaLevel || IsLake(index);
    public bool IsRiver(int index) => !IsWater(index) && Runoff[index] >= MinimumRiverRunoff;
    public bool IsFreshWater(int index) => IsLake(index) || IsRiver(index);

    public static SphericalHydrology Build(SphericalWorldDefinition definition,
        SphericalTerrainGenerator terrain, int stride = 1)
    {
        if (stride < 1 || definition.FaceSize % stride != 0 || definition.FaceSize / stride < 2)
            throw new ArgumentOutOfRangeException(nameof(stride));
        var topology = new CubeSphereTopology(definition.FaceSize / stride);
        // Historical runoff units were one 4x4 block of microcells. Keep river
        // thresholds and domestic access comparable when refining that grid.
        return FromSamples(topology.FaceSize, (float)definition.Terrain.SeaLevelMeters,
            cell => terrain.SampleSurface(topology.ToUnitVector(cell)), stride * stride / 16f);
    }

    public static SphericalHydrology FromSamples(int resolution, float seaLevel,
        Func<CellAddress, TerrainCellSample> sample, float runoffWeight = 1)
    {
        var topology = new CubeSphereTopology(resolution);
        if (!float.IsFinite(seaLevel)) throw new ArgumentOutOfRangeException(nameof(seaLevel));
        if (!float.IsFinite(runoffWeight) || runoffWeight <= 0) throw new ArgumentOutOfRangeException(nameof(runoffWeight));
        var count = checked((int)topology.CellCount);
        var elevation = new float[count];
        var moisture = new float[count];
        for (var index = 0; index < count; index++)
        {
            var cell = new CellAddress((CubeFace)(index / (resolution * resolution)),
                index % resolution, index / resolution % resolution);
            var value = sample(cell);
            if (!float.IsFinite(value.ElevationMeters) || !float.IsFinite(value.Moisture))
                throw new ArgumentException("Hydrology requires finite terrain samples", nameof(sample));
            elevation[index] = value.ElevationMeters;
            moisture[index] = Math.Clamp(value.Moisture, 0, 1);
        }
        return new SphericalHydrology(resolution, seaLevel, elevation, moisture, runoffWeight);
    }

    private int NeighborIndices(int index, Span<int> result)
    {
        var x = index % Resolution; var y = index / Resolution % Resolution;
        if (x > 0 && y > 0 && x < Resolution - 1 && y < Resolution - 1)
        {
            result[0] = index - Resolution - 1; result[1] = index - Resolution; result[2] = index - Resolution + 1;
            result[3] = index - 1; result[4] = index + 1;
            result[5] = index + Resolution - 1; result[6] = index + Resolution; result[7] = index + Resolution + 1;
            return 8;
        }
        var count = 0;
        foreach (var cell in GetDrainageNeighbors(Address(index))) result[count++] = Index(cell);
        return count;
    }

    private float[] BuildLakeShore()
    {
        var field = new float[Elevation.Length];
        Span<int> neighbors = stackalloc int[8];
        for (var i = 0; i < field.Length; i++)
        {
            var level = Surface[i] > Elevation[i] && Elevation[i] > SeaLevel ? Surface[i] : float.NegativeInfinity;
            if (!float.IsFinite(level))
            {
                var count = NeighborIndices(i, neighbors);
                for (var n = 0; n < count; n++)
                {
                    var j = neighbors[n];
                    if (Surface[j] > Elevation[j] && Elevation[j] > SeaLevel) level = Math.Max(level, Surface[j]);
                }
            }
            // Far inland the sentinel is finite and dry. A dry center can never
            // be reclassified by a neighboring, higher basin across a ridge.
            var signed = float.IsFinite(level) ? level - Elevation[i] - LakeDepthThreshold : -LakeDepthThreshold;
            field[i] = IsLake(i) ? Surface[i] - Elevation[i] - LakeDepthThreshold : Math.Min(0, signed);
        }
        return field;
    }

    public int Index(CellAddress cell) => ((int)cell.Face * Resolution + cell.Y) * Resolution + cell.X;
    public CellAddress Address(int index) => new((CubeFace)(index / (Resolution * Resolution)),
        index % Resolution, index / Resolution % Resolution);

    public IReadOnlyList<CellAddress> GetDrainageNeighbors(CellAddress cell)
    {
        var neighbors = new HashSet<CellAddress>(Topology.GetNeighbors(cell));
        foreach (var dx in new[] { -1, 1 })
            foreach (var dy in new[] { -1, 1 })
                neighbors.Add(Topology.Locate(CubeSphereTopology.ProjectFacePoint(cell.Face,
                    -1 + (cell.X + dx + 0.5) * 2 / Resolution,
                    -1 + (cell.Y + dy + 0.5) * 2 / Resolution)));
        neighbors.Remove(cell);
        return neighbors.OrderBy(Index).ToArray();
    }

    // Reach endpoints are retained at junctions. Tributaries share the same endpoint;
    // smoothing in the renderer must not move these endpoints apart.
    public IReadOnlyList<DrainageReach> BuildReaches(float minimumRunoff = MinimumRiverRunoff)
    {
        if (!float.IsFinite(minimumRunoff) || minimumRunoff <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumRunoff));
        var active = new bool[Runoff.Length];
        var incoming = new int[Runoff.Length];
        for (var index = 0; index < active.Length; index++)
        {
            // Flooded depressions are lakes, not visible river channels. Keep the
            // hydrologic flow through them, but split cartographic reaches at shore.
            active[index] = !IsWater(index) &&
                Downstream[index] >= 0 && Runoff[index] >= minimumRunoff;
            if (active[index]) incoming[Downstream[index]]++;
        }
        var reaches = new List<DrainageReach>();
        for (var index = 0; index < active.Length; index++)
        {
            if (!active[index] || incoming[index] == 1) continue;
            var points = new List<UnitVector3> { Topology.ToUnitVector(Address(index)) };
            var current = index;
            var flow = Runoff[index];
            do
            {
                flow = Math.Max(flow, Runoff[current]);
                current = Downstream[current];
                points.Add(Topology.ToUnitVector(Address(current)));
            } while (active[current] && incoming[current] == 1);
            reaches.Add(new DrainageReach(index, flow, points));
        }
        return reaches;
    }
}

public sealed record DrainageReach(int Id, float Runoff, IReadOnlyList<UnitVector3> Points);
