namespace WorldGen.Core.Topology;

public sealed record SurfaceWaterTerrainUpdate(
    uint Revision,
    int ChangedCells,
    int OceanCellsAdded,
    int OceanCellsRemoved,
    double OceanVolumeDeltaCubicMeters,
    double InlandVolumeDeltaCubicMeters,
    IReadOnlyCollection<CellAddress> ChangedWaterCells);

public sealed record SurfaceWaterWeatherForcing(
    int Resolution,
    IReadOnlyList<float> LiquidPrecipitationMillimeters,
    IReadOnlyList<float> PotentialEvaporationMillimeters);

public sealed record SurfaceWaterStepResult(
    uint Revision,
    int ChangedCells,
    double PrecipitationCubicMeters,
    double InfiltrationCubicMeters,
    double GroundwaterRechargeCubicMeters,
    double SpringDischargeCubicMeters,
    double EvaporationCubicMeters,
    double OceanExchangeCubicMeters,
    double StorageDeltaCubicMeters,
    double BalanceErrorCubicMeters,
    IReadOnlyCollection<CellAddress> ChangedWaterCells,
    IReadOnlyDictionary<CellAddress, int> ChannelTerrainChanges)
{
    public static SurfaceWaterStepResult Empty(uint revision) => new(revision, 0, 0, 0, 0, 0, 0, 0, 0, 0, [],
        new Dictionary<CellAddress, int>());
}

public enum DynamicRiverClass : byte { None, Small, Medium, Major }

public sealed record DynamicRiverReach(
    int Id,
    float DischargeCubicMetersPerDay,
    float WidthMeters,
    DynamicRiverClass Class,
    IReadOnlyList<UnitVector3> Points);

/// <summary>
/// The authoritative surface-water field. Elevation below mean sea level is only
/// ocean when it belongs to the largest cardinally connected below-sea component.
/// Inland water is initialised from the drainage basins and keeps its volume while
/// terrain changes; redistribution is deliberately a later hydrologic step.
/// </summary>
public sealed class SurfaceWaterState
{
    public const float MinimumChannelDischargeCubicMetersPerDay = 400;
    public const float MinimumRenderedRiverDischargeCubicMetersPerDay = 900;
    public const float OpenWaterDepthMeters = .20f;
    public const double SedimentVolumePerWaterVolume = .01;
    public const int StandingWaterDepositRadiusMinimumCells = 4;
    public const int StandingWaterDepositRadiusMaximumCells = 9;
    private readonly CubeSphereTopology topology;
    private readonly double cellAreaSquareMeters;
    private readonly int faceLength;
    private readonly HashSet<int> active = [];
    private readonly HashSet<int> activeGroundwater = [];
    private readonly float[] flowDelta;
    private readonly float[] dailySurfaceOutflow;
    private readonly float[] dailyDominantFlow;
    private readonly int[] dailySurfaceTarget;
    private readonly HashSet<int> dailyFlowTouched = [];
    private readonly HashSet<int> channelActive = [];
    private readonly HashSet<int> monthlyChannelActive = [];
    private readonly double[] monthlyChannelVolume;
    private readonly double[] erosionRemainderCellCentimeters;
    private int daysAdvanced;
    private const float SoilCapacityMeters = .25f;
    private const float SoilFieldCapacityMeters = .14f;
    private const float DailyInfiltrationCapacityMeters = .045f;
    private const float DailyPercolationCapacityMeters = .006f;
    private const float GroundwaterSpecificYield = .2f;
    private const float VisibleDepthEpsilon = .0001f;

    private SurfaceWaterState(SphericalHydrology hydrology, double cellAreaSquareMeters)
    {
        if (!double.IsFinite(cellAreaSquareMeters) || cellAreaSquareMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellAreaSquareMeters));
        topology = hydrology.Topology;
        this.cellAreaSquareMeters = cellAreaSquareMeters;
        faceLength = checked(topology.FaceSize * topology.FaceSize);
        SeaLevel = hydrology.SeaLevel;
        Elevation = (float[])hydrology.Elevation.Clone();
        Depth = new float[Elevation.Length];
        SoilWater = new float[Elevation.Length];
        GroundwaterStorage = new float[Elevation.Length];
        AquiferBase = new float[Elevation.Length];
        Shore = new float[Elevation.Length];
        flowDelta = new float[Elevation.Length];
        dailySurfaceOutflow = new float[Elevation.Length];
        dailyDominantFlow = new float[Elevation.Length];
        dailySurfaceTarget = Enumerable.Repeat(-1, Elevation.Length).ToArray();
        SmoothedDischarge = new float[Elevation.Length];
        ChannelCapacity = new float[Elevation.Length];
        ChannelTarget = Enumerable.Repeat(-1, Elevation.Length).ToArray();
        channelCandidate = Enumerable.Repeat(-1, Elevation.Length).ToArray();
        channelCandidateDays = new byte[Elevation.Length];
        channelVisualClass = new byte[Elevation.Length];
        monthlyChannelVolume = new double[Elevation.Length];
        erosionRemainderCellCentimeters = new double[Elevation.Length];
        ChannelIncision = new float[Elevation.Length];
        Ocean = BuildOceanMask();

        for (var index = 0; index < Depth.Length; index++)
        {
            var moisture = Math.Clamp(hydrology.Moisture[index], 0, 1);
            // Climate moisture describes the ordinary retained state, not a
            // completely saturated soil column. Leave pore capacity for rain.
            SoilWater[index] = moisture * SoilFieldCapacityMeters;
            var aquiferThickness = 12 + moisture * 18;
            AquiferBase[index] = Elevation[index] - aquiferThickness;
            var depthToWater = 12 - moisture * 10;
            GroundwaterStorage[index] = Math.Max(0, (aquiferThickness - depthToWater) * GroundwaterSpecificYield);
            if (Ocean[index])
                Depth[index] = Math.Max(0, SeaLevel - Elevation[index]);
            else if (hydrology.IsLake(index))
            {
                Depth[index] = Math.Max(0, hydrology.Surface[index] - Elevation[index] - SphericalHydrology.LakeDepthThreshold);
            }
            if (!Ocean[index] && Depth[index] > VisibleDepthEpsilon)
            {
                SoilWater[index] = SoilCapacityMeters;
                GroundwaterStorage[index] = Math.Max(GroundwaterStorage[index],
                    (Elevation[index] - AquiferBase[index]) * GroundwaterSpecificYield);
            }
            if (!Ocean[index] && Depth[index] > VisibleDepthEpsilon)
            {
                active.Add(index);
            }
        }
        RebuildShore();
    }

    public static SurfaceWaterState FromHydrology(SphericalHydrology hydrology, double cellAreaSquareMeters)
    {
        ArgumentNullException.ThrowIfNull(hydrology);
        return new(hydrology, cellAreaSquareMeters);
    }

    public static double ErodedSedimentCubicMeters(double waterVolumeCubicMeters, double groundErodibility = 1) =>
        Math.Max(0, waterVolumeCubicMeters) * SedimentVolumePerWaterVolume * Math.Max(0, groundErodibility);

    public static int StandingWaterDepositRadiusCells(double sedimentCubicMeters) =>
        Math.Clamp(StandingWaterDepositRadiusMinimumCells +
            (int)Math.Floor(Math.Log10(Math.Max(1, sedimentCubicMeters / 100d))),
            StandingWaterDepositRadiusMinimumCells, StandingWaterDepositRadiusMaximumCells);

    public int Resolution => topology.FaceSize;
    public float SeaLevel { get; }
    public uint Revision { get; private set; }
    public float[] Elevation { get; }
    public float[] Depth { get; }
    public float[] SoilWater { get; }
    public float[] GroundwaterStorage { get; }
    public float[] AquiferBase { get; }
    public float[] Shore { get; }
    public bool[] Ocean { get; private set; }
    public float[] SmoothedDischarge { get; }
    public float[] ChannelCapacity { get; }
    public int[] ChannelTarget { get; }
    public float[] ChannelIncision { get; }
    private readonly int[] channelCandidate;
    private readonly byte[] channelCandidateDays;
    private readonly byte[] channelVisualClass;
    public uint RiverRevision { get; private set; }

    public int Index(CellAddress cell) => ((int)cell.Face * Resolution + cell.Y) * Resolution + cell.X;
    public CellAddress Address(int index) => new((CubeFace)(index / faceLength), index % Resolution, index / Resolution % Resolution);
    public bool IsOcean(CellAddress cell) => Ocean[Index(cell)];
    public bool IsWet(CellAddress cell) => Depth[Index(cell)] > 0;
    public float DepthAt(CellAddress cell) => Depth[Index(cell)];
    public float ShoreAt(CellAddress cell) => Shore[Index(cell)];
    public bool IsOpenWater(CellAddress cell) => IsOpenWater(Index(cell));
    public bool IsRiver(CellAddress cell)
    {
        var index = Index(cell);
        return !Ocean[index] && !IsOpenWater(index) && ChannelTarget[index] >= 0 &&
            SmoothedDischarge[index] >= MinimumChannelDischargeCubicMetersPerDay;
    }
    public float GroundwaterHeadAt(CellAddress cell)
    {
        var index = Index(cell);
        return AquiferBase[index] + GroundwaterStorage[index] / GroundwaterSpecificYield;
    }

    /// <summary>
    /// Advances the fast surface-water layer by one day. Rain and evaporation
    /// are sampled from the coarse atmospheric grid, while runoff remains on
    /// the exact terrain grid. Transfers are conservative; infiltration merely
    /// moves water between surface and soil storage.
    /// </summary>
    public SurfaceWaterStepResult AdvanceDay(SurfaceWaterWeatherForcing forcing, int flowSubsteps = 4)
    {
        ArgumentNullException.ThrowIfNull(forcing);
        var forcingLength = checked(6 * forcing.Resolution * forcing.Resolution);
        if (forcing.Resolution < 1 || forcing.LiquidPrecipitationMillimeters.Count != forcingLength ||
            forcing.PotentialEvaporationMillimeters.Count != forcingLength)
            throw new ArgumentException("Некорректная сетка погоды", nameof(forcing));
        if (flowSubsteps is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(flowSubsteps));

        foreach (var index in dailyFlowTouched)
        {
            dailySurfaceOutflow[index] = 0;
            dailyDominantFlow[index] = 0;
            dailySurfaceTarget[index] = -1;
        }
        dailyFlowTouched.Clear();

        var storageBefore = InlandStorageVolume();
        double precipitation = 0, infiltration = 0, groundwaterRecharge = 0, springDischarge = 0,
            evaporation = 0, oceanExchange = 0;
        var changed = new HashSet<int>();

        for (var index = 0; index < Depth.Length; index++)
        {
            if (Ocean[index]) continue;
            var weather = WeatherIndex(index, forcing.Resolution);
            var rain = Math.Max(0, forcing.LiquidPrecipitationMillimeters[weather]) / 1000f;
            var evaporativeDemand = Math.Max(0, forcing.PotentialEvaporationMillimeters[weather]) / 1000f;
            precipitation += rain * cellAreaSquareMeters;

            var surfaceEvaporation = Math.Min(Depth[index], evaporativeDemand);
            if (surfaceEvaporation > 0)
            {
                Depth[index] -= surfaceEvaporation;
                evaporativeDemand -= surfaceEvaporation;
                evaporation += surfaceEvaporation * cellAreaSquareMeters;
                changed.Add(index);
            }
            var soilEvaporation = Math.Min(SoilWater[index], evaporativeDemand);
            if (soilEvaporation > 0)
            {
                SoilWater[index] -= soilEvaporation;
                evaporation += soilEvaporation * cellAreaSquareMeters;
            }

            // Rain joins any water already lying on the cell. Unsaturated soil
            // can therefore drain an old sheet as well as absorb a new shower.
            var surfaceAvailable = Depth[index] + rain;
            var absorbed = Math.Min(surfaceAvailable,
                Math.Min(DailyInfiltrationCapacityMeters, Math.Max(0, SoilCapacityMeters - SoilWater[index])));
            if (absorbed > 0)
            {
                SoilWater[index] += absorbed;
                infiltration += absorbed * cellAreaSquareMeters;
            }
            var nextDepth = Math.Max(0, surfaceAvailable - absorbed);
            if (Math.Abs(nextDepth - Depth[index]) > 1e-9f)
            {
                Depth[index] = nextDepth;
                changed.Add(index);
            }
            if (Depth[index] > VisibleDepthEpsilon) active.Add(index);

            // Water above field capacity percolates slowly. The soil can accept
            // a storm quickly, but it cannot teleport that volume into an aquifer.
            var saturatedStorage = Math.Max(0, (Elevation[index] - AquiferBase[index]) * GroundwaterSpecificYield);
            var recharge = Math.Min(Math.Max(0, SoilWater[index] - SoilFieldCapacityMeters),
                Math.Min(DailyPercolationCapacityMeters, Math.Max(0, saturatedStorage - GroundwaterStorage[index])));
            if (recharge > 0)
            {
                SoilWater[index] -= recharge;
                GroundwaterStorage[index] += recharge;
                groundwaterRecharge += recharge * cellAreaSquareMeters;
                activeGroundwater.Add(index);
            }
        }

        Span<int> neighbors = stackalloc int[4];
        // Groundwater uses the same conservative scratch array as surface flow,
        // but has a much smaller daily transfer fraction. It therefore develops
        // gradients and springs instead of instantly levelling a whole basin.
        if (activeGroundwater.Count > 0)
        {
            var groundSources = activeGroundwater.ToArray();
            var groundTouched = new HashSet<int>();
            foreach (var source in groundSources)
            {
                if (Ocean[source] || GroundwaterStorage[source] <= 0) continue;
                var head = GroundwaterHead(source);
                var target = -1; var targetHead = head;
                var neighborCount = CardinalNeighborIndices(source, neighbors);
                for (var n = 0; n < neighborCount; n++)
                {
                    var candidate = neighbors[n];
                    var candidateHead = Ocean[candidate] ? SeaLevel : GroundwaterHead(candidate);
                    if (candidateHead < targetHead)
                    {
                        targetHead = candidateHead;
                        target = candidate;
                    }
                }
                if (target < 0) continue;
                var transfer = Math.Min(GroundwaterStorage[source] * .0015f,
                    (head - targetHead) * GroundwaterSpecificYield * .025f);
                if (transfer <= 1e-7f) continue;
                flowDelta[source] -= transfer;
                groundTouched.Add(source);
                if (Ocean[target]) oceanExchange += transfer * cellAreaSquareMeters;
                else
                {
                    flowDelta[target] += transfer;
                    groundTouched.Add(target);
                }
            }

            activeGroundwater.Clear();
            foreach (var index in groundTouched)
            {
                var delta = flowDelta[index]; flowDelta[index] = 0;
                if (Math.Abs(delta) <= 1e-9f) continue;
                GroundwaterStorage[index] = Math.Max(0, GroundwaterStorage[index] + delta);
                activeGroundwater.Add(index);
            }

            // Oversaturated groundwater reaches the surface as a finite spring.
            // Sources with no lower underground neighbour must still be tested:
            // this is the usual case after a crater exposes the local head.
            foreach (var index in groundSources.Concat(groundTouched).Distinct())
            {
                if (Ocean[index]) continue;
                var saturatedStorage = Math.Max(0, (Elevation[index] - AquiferBase[index]) * GroundwaterSpecificYield);
                var excess = GroundwaterStorage[index] - saturatedStorage;
                if (excess <= 1e-7f) continue;
                var discharge = Math.Min(excess, .003f + excess * .1f);
                GroundwaterStorage[index] -= discharge;
                Depth[index] += discharge;
                springDischarge += discharge * cellAreaSquareMeters;
                changed.Add(index);
                active.Add(index);
                if (GroundwaterStorage[index] - saturatedStorage > 1e-7f) activeGroundwater.Add(index);
            }
        }

        for (var substep = 0; substep < flowSubsteps && active.Count > 0; substep++)
        {
            var sources = active.ToArray();
            var touched = new HashSet<int>();
            foreach (var source in sources)
            {
                if (Ocean[source] || Depth[source] <= VisibleDepthEpsilon) continue;
                var surface = Elevation[source] + Depth[source];
                var target = -1;
                var targetSurface = surface;
                var neighborCount = CardinalNeighborIndices(source, neighbors);
                for (var n = 0; n < neighborCount; n++)
                {
                    var candidate = neighbors[n];
                    var candidateSurface = Ocean[candidate] ? SeaLevel : Elevation[candidate] + Depth[candidate];
                    if (candidateSurface < targetSurface)
                    {
                        targetSurface = candidateSurface;
                        target = candidate;
                    }
                }
                var retainedTarget = ChannelTarget[source];
                if (retainedTarget >= 0)
                {
                    for (var n = 0; n < neighborCount; n++)
                    {
                        if (neighbors[n] != retainedTarget) continue;
                        var retainedSurface = Ocean[retainedTarget] ? SeaLevel : Elevation[retainedTarget] + Depth[retainedTarget];
                        // A shallow competing advantage does not move an
                        // established river sideways after every rain pulse.
                        if (retainedSurface < surface && retainedSurface <= targetSurface + .15f)
                        {
                            target = retainedTarget;
                            targetSurface = retainedSurface;
                        }
                        break;
                    }
                }
                if (target < 0) continue;
                var head = surface - targetSurface;
                var transfer = Math.Min(Depth[source] * .45f, head * .2f);
                if (transfer <= VisibleDepthEpsilon) continue;
                var transferredVolume = (float)(transfer * cellAreaSquareMeters);
                dailySurfaceOutflow[source] += transferredVolume;
                if (transferredVolume > dailyDominantFlow[source])
                {
                    dailyDominantFlow[source] = transferredVolume;
                    dailySurfaceTarget[source] = target;
                }
                dailyFlowTouched.Add(source);
                flowDelta[source] -= transfer;
                touched.Add(source);
                if (Ocean[target]) oceanExchange += transfer * cellAreaSquareMeters;
                else
                {
                    flowDelta[target] += transfer;
                    touched.Add(target);
                }
            }

            active.Clear();
            foreach (var index in touched)
            {
                var delta = flowDelta[index];
                flowDelta[index] = 0;
                if (Math.Abs(delta) <= 1e-9f) continue;
                Depth[index] = Math.Max(0, Depth[index] + delta);
                changed.Add(index);
                if (!Ocean[index] && Depth[index] > VisibleDepthEpsilon) active.Add(index);
                var neighborCount = CardinalNeighborIndices(index, neighbors);
                for (var n = 0; n < neighborCount; n++)
                    if (!Ocean[neighbors[n]] && Depth[neighbors[n]] > VisibleDepthEpsilon) active.Add(neighbors[n]);
            }
            // A lake in local equilibrium must remain eligible after weather or
            // a later inflow changes its head, even if this substep had no flux.
            foreach (var index in sources)
                if (!Ocean[index] && Depth[index] > VisibleDepthEpsilon) active.Add(index);
        }

        UpdateChannels();
        daysAdvanced++;
        var channelTerrainChanges = daysAdvanced % 30 == 0
            ? BuildMonthlyChannelTerrainChanges()
            : new Dictionary<CellAddress, int>();

        if (changed.Count > 0)
        {
            var shoreDirty = changed.ToHashSet();
            Span<int> shoreNeighbors = stackalloc int[8];
            foreach (var index in changed)
            {
                var count = ShoreNeighborIndices(index, shoreNeighbors);
                for (var n = 0; n < count; n++) shoreDirty.Add(shoreNeighbors[n]);
            }
            RebuildShore(shoreDirty);
            changed = shoreDirty;
            Revision++;
        }

        var storageDelta = InlandStorageVolume() - storageBefore;
        var balanceError = storageDelta - (precipitation - evaporation - oceanExchange);
        return new(Revision, changed.Count, precipitation, infiltration, groundwaterRecharge, springDischarge,
            evaporation, oceanExchange,
            storageDelta, balanceError, changed.Select(Address).ToArray(), channelTerrainChanges);
    }

    private void UpdateChannels()
    {
        var candidates = channelActive.Concat(dailyFlowTouched).Distinct().ToArray();
        channelActive.Clear();
        var visualChanged = false;
        foreach (var index in candidates)
        {
            var observed = dailySurfaceOutflow[index];
            monthlyChannelVolume[index] += observed;
            if (observed > 0) monthlyChannelActive.Add(index);
            var smoothed = SmoothedDischarge[index];
            smoothed += (observed - smoothed) * (observed > smoothed ? .2f : .035f);
            if (smoothed < .01f) smoothed = 0;
            SmoothedDischarge[index] = smoothed;

            var capacity = ChannelCapacity[index];
            capacity += (smoothed - capacity) * (smoothed > capacity ? .04f : .003f);
            if (capacity < .01f) capacity = 0;
            ChannelCapacity[index] = capacity;

            var observedTarget = dailySurfaceTarget[index];
            if (observed >= 5 && observedTarget >= 0 && observedTarget != ChannelTarget[index])
            {
                if (channelCandidate[index] == observedTarget)
                    channelCandidateDays[index] = (byte)Math.Min(byte.MaxValue, channelCandidateDays[index] + 1);
                else
                {
                    channelCandidate[index] = observedTarget;
                    channelCandidateDays[index] = 1;
                }
                var requiredDays = ChannelTarget[index] < 0 ? 5 : 7;
                if (channelCandidateDays[index] >= requiredDays)
                {
                    ChannelTarget[index] = observedTarget;
                    channelCandidate[index] = -1;
                    channelCandidateDays[index] = 0;
                    visualChanged = true;
                }
            }
            else if (observedTarget == ChannelTarget[index])
            {
                channelCandidate[index] = -1;
                channelCandidateDays[index] = 0;
            }

            // Capacity is geomorphic memory used for routing. Blue water is a
            // present flow: an old dry channel must not remain a permanent river.
            var displayDischarge = smoothed;
            var nextClass = ChannelIncision[index] >= .015f
                ? (byte)RiverClass(displayDischarge, (DynamicRiverClass)channelVisualClass[index])
                : (byte)DynamicRiverClass.None;
            if (ChannelTarget[index] < 0) nextClass = (byte)DynamicRiverClass.None;
            if (channelVisualClass[index] != nextClass)
            {
                channelVisualClass[index] = nextClass;
                visualChanged = true;
            }
            if (nextClass == (byte)DynamicRiverClass.None && smoothed < 1 && capacity < 5)
            {
                if (ChannelTarget[index] >= 0) visualChanged = true;
                ChannelTarget[index] = -1;
            }
            if (observed > 0 || smoothed >= 1 || capacity >= 5) channelActive.Add(index);
        }
        if (visualChanged) RiverRevision++;
    }

    private IReadOnlyDictionary<CellAddress, int> BuildMonthlyChannelTerrainChanges()
    {
        var deltas = new Dictionary<int, int>();
        var localSediment = new Dictionary<int, int>();
        var carriedSediment = new Dictionary<int, long>();
        var incoming = new Dictionary<int, int>();
        var dominantUpstream = new Dictionary<int, int>();
        var flowedCells = monthlyChannelActive.ToArray();
        monthlyChannelActive.Clear();
        var minimumMonthlyVolume = MinimumChannelDischargeCubicMetersPerDay * 30d;

        foreach (var index in flowedCells)
        {
            var volume = monthlyChannelVolume[index];
            monthlyChannelVolume[index] = 0;
            var target = ChannelTarget[index];
            if (target < 0 || IsOpenWater(index) || volume < minimumMonthlyVolume) continue;

            // Each channel cell entrains one cubic metre of soil per one
            // hundred cubic metres of monthly flow. Terrain is stored at one
            // centimetre precision, so a sub-centimetre balance remains in
            // this cell and participates in the next monthly pass.
            var erodedCellCentimeters = ErodedSedimentCubicMeters(volume) / cellAreaSquareMeters * 100d
                + erosionRemainderCellCentimeters[index];
            var erosionCentimeters = (int)Math.Min(int.MaxValue, Math.Floor(erodedCellCentimeters));
            erosionRemainderCellCentimeters[index] = erodedCellCentimeters - erosionCentimeters;
            if (erosionCentimeters <= 0) continue;
            localSediment.Add(index, erosionCentimeters);
            ChannelIncision[index] += erosionCentimeters / 100f;
            AddTerrainDelta(deltas, index, -erosionCentimeters);

            // The monthly geomorphology pass is what turns concentrated runoff
            // into a visible bed. Publish that transition in the same step rather
            // than waiting for tomorrow's hydrology update.
            var nextClass = ChannelTarget[index] >= 0
                ? (byte)RiverClass(SmoothedDischarge[index], (DynamicRiverClass)channelVisualClass[index])
                : (byte)DynamicRiverClass.None;
            if (channelVisualClass[index] != nextClass)
                channelVisualClass[index] = nextClass;
        }

        // Follow only this month's wet channel graph. Targets are discovered
        // lazily, so the geomorphology pass allocates by river length rather
        // than by the complete million-cell surface.
        var graphNodes = localSediment.Keys.ToHashSet();
        var discover = new Queue<int>(graphNodes);
        while (discover.TryDequeue(out var index))
        {
            var target = ChannelTarget[index];
            if (target < 0) continue;
            incoming[target] = incoming.GetValueOrDefault(target) + 1;
            incoming.TryAdd(index, incoming.GetValueOrDefault(index));
            var currentUpstream = dominantUpstream.GetValueOrDefault(target, -1);
            if (currentUpstream < 0 || SmoothedDischarge[index] > SmoothedDischarge[currentUpstream])
                dominantUpstream[target] = index;
            if (graphNodes.Add(target) && !IsOpenWater(target)) discover.Enqueue(target);
        }

        var queue = new Queue<int>(graphNodes.Where(index => incoming.GetValueOrDefault(index) == 0));
        var processed = new HashSet<int>();
        while (queue.TryDequeue(out var index))
        {
            processed.Add(index);
            var target = ChannelTarget[index];
            var sediment = carriedSediment.GetValueOrDefault(index) + localSediment.GetValueOrDefault(index);
            if (sediment > 0)
            {
                if (target < 0) AddTerrainDelta(deltas, index, sediment);
                else if (IsOpenWater(target)) DepositIntoStandingWater(deltas, target, sediment);
                else
                {
                    var upstream = dominantUpstream.GetValueOrDefault(index, -1);
                    var innerBank = upstream >= 0 && IsChannelTurn(upstream, index, target)
                        ? InnerBankCell(upstream, index, target)
                        : -1;
                    if (innerBank >= 0) AddTerrainDelta(deltas, innerBank, sediment);
                    else carriedSediment[target] = carriedSediment.GetValueOrDefault(target) + sediment;
                }
            }

            if (target >= 0 && incoming.ContainsKey(target) && --incoming[target] == 0) queue.Enqueue(target);
        }

        // Downhill routing should be acyclic. Channel hysteresis can preserve
        // a short cycle immediately after deformation; keep that mass local for
        // this month instead of duplicating or losing it.
        foreach (var index in graphNodes)
        {
            var sediment = carriedSediment.GetValueOrDefault(index) + localSediment.GetValueOrDefault(index);
            if (!processed.Contains(index) && sediment > 0) AddTerrainDelta(deltas, index, sediment);
        }

        var compact = deltas.Where(pair => pair.Value != 0)
            .ToDictionary(pair => Address(pair.Key), pair => pair.Value);
        if (compact.Count > 0) RiverRevision++;
        return compact;
    }

    private void DepositIntoStandingWater(Dictionary<int, int> deltas, int inlet, long sedimentCellCentimeters)
    {
        var sedimentCubicMeters = sedimentCellCentimeters * cellAreaSquareMeters / 100d;
        var radius = StandingWaterDepositRadiusCells(sedimentCubicMeters);
        var cells = new List<int>();
        var visited = new HashSet<int> { inlet };
        var queue = new Queue<(int Index, int Distance)>();
        queue.Enqueue((inlet, 0));
        Span<int> neighbors = stackalloc int[4];
        while (queue.TryDequeue(out var item))
        {
            if (!IsOpenWater(item.Index)) continue;
            cells.Add(item.Index);
            if (item.Distance >= radius) continue;
            var count = CardinalNeighborIndices(item.Index, neighbors);
            for (var n = 0; n < count; n++)
            {
                var neighbor = neighbors[n];
                if (visited.Add(neighbor) && IsOpenWater(neighbor)) queue.Enqueue((neighbor, item.Distance + 1));
            }
        }
        if (cells.Count == 0) { AddTerrainDelta(deltas, inlet, sedimentCellCentimeters); return; }
        var quotient = sedimentCellCentimeters / cells.Count;
        var remainder = sedimentCellCentimeters % cells.Count;
        for (var index = 0; index < cells.Count; index++)
            AddTerrainDelta(deltas, cells[index], quotient + (index < remainder ? 1 : 0));
    }

    private int InnerBankCell(int upstream, int current, int target)
    {
        var a = topology.ToUnitVector(Address(upstream));
        var b = topology.ToUnitVector(Address(current));
        var c = topology.ToUnitVector(Address(target));
        var inward = UnitVector3.Normalize(a.X + c.X - b.X * 2, a.Y + c.Y - b.Y * 2, a.Z + c.Z - b.Z * 2);
        Span<int> neighbors = stackalloc int[4];
        var count = CardinalNeighborIndices(current, neighbors);
        var best = -1; var bestScore = double.NegativeInfinity;
        for (var n = 0; n < count; n++)
        {
            var candidate = neighbors[n];
            if (candidate == upstream || candidate == target || IsOpenWater(candidate)) continue;
            var point = topology.ToUnitVector(Address(candidate));
            var direction = UnitVector3.Normalize(point.X - b.X, point.Y - b.Y, point.Z - b.Z);
            var score = direction.Dot(inward);
            if (score > bestScore) { best = candidate; bestScore = score; }
        }
        return bestScore > 0 ? best : -1;
    }

    private static void AddTerrainDelta(Dictionary<int, int> deltas, int index, long centimeters)
    {
        if (centimeters == 0) return;
        deltas[index] = checked(deltas.GetValueOrDefault(index) + (int)centimeters);
    }

    private bool IsChannelTurn(int upstream, int current, int target)
    {
        var a = topology.ToUnitVector(Address(upstream));
        var b = topology.ToUnitVector(Address(current));
        var c = topology.ToUnitVector(Address(target));
        var incoming = UnitVector3.Normalize(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var outgoing = UnitVector3.Normalize(c.X - b.X, c.Y - b.Y, c.Z - b.Z);
        return incoming.Dot(outgoing) < .75;
    }

    public IReadOnlyList<DynamicRiverReach> BuildRiverReaches()
    {
        var activeEdges = Enumerable.Range(0, ChannelTarget.Length)
            .Where(index => ChannelTarget[index] >= 0 && channelVisualClass[index] != (byte)DynamicRiverClass.None &&
                SmoothedDischarge[index] >= MinimumRenderedRiverDischargeCubicMetersPerDay &&
                !Ocean[index] && !IsOpenWater(index))
            .ToArray();
        if (activeEdges.Length == 0) return [];
        var active = activeEdges.ToHashSet();
        var incoming = new int[ChannelTarget.Length];
        foreach (var index in activeEdges)
            if (active.Contains(ChannelTarget[index])) incoming[ChannelTarget[index]]++;
        var visited = new HashSet<int>();
        var reaches = new List<DynamicRiverReach>();

        void AddReach(int start)
        {
            if (visited.Contains(start) || !active.Contains(start)) return;
            var riverClass = (DynamicRiverClass)channelVisualClass[start];
            var points = new List<UnitVector3> { topology.ToUnitVector(Address(start)) };
            var current = start; var maximumDischarge = 0f;
            while (active.Contains(current) && !visited.Contains(current) &&
                (DynamicRiverClass)channelVisualClass[current] == riverClass)
            {
                visited.Add(current);
                maximumDischarge = Math.Max(maximumDischarge,
                    Math.Max(SmoothedDischarge[current], ChannelCapacity[current] * .55f));
                var next = ChannelTarget[current];
                if (next < 0) break;
                points.Add(!IsOpenWater(current) && IsOpenWater(next)
                    ? ShoreIntersection(current, next)
                    : topology.ToUnitVector(Address(next)));
                if (!IsOpenWater(current) && IsOpenWater(next)) break;
                if (!active.Contains(next) || incoming[next] != 1 ||
                    (DynamicRiverClass)channelVisualClass[next] != riverClass) break;
                current = next;
            }
            if (points.Count >= 2)
                reaches.Add(new(start, maximumDischarge, RiverWidthMeters(maximumDischarge), riverClass, points));
        }

        foreach (var start in activeEdges.Where(index => incoming[index] != 1).Order()) AddReach(start);
        foreach (var start in activeEdges.Order()) AddReach(start);
        return reaches;
    }

    private UnitVector3 ShoreIntersection(int dryIndex, int wetIndex)
    {
        var dry = topology.ToUnitVector(Address(dryIndex));
        var wet = topology.ToUnitVector(Address(wetIndex));
        var dryShore = Math.Min(-.001f, Shore[dryIndex]);
        var wetShore = Math.Max(.001f, Shore[wetIndex]);
        var amount = Math.Clamp(-dryShore / (wetShore - dryShore), .02f, .98f);
        // The standing-water fill is bilinearly filtered and the retained GL
        // river quad has a flat cap. Ending at the mathematical zero therefore
        // exposes a pale gap around sharp shores. Hide the cap just inside the
        // first wet cell; the overlap is water-coloured and remains stable while
        // the signed shore field animates between daily states.
        amount = Math.Min(.98f, amount + (1 - amount) * .55f);
        return UnitVector3.Normalize(
            dry.X + (wet.X - dry.X) * amount,
            dry.Y + (wet.Y - dry.Y) * amount,
            dry.Z + (wet.Z - dry.Z) * amount);
    }

    private bool IsOpenWater(int index)
    {
        if (Ocean[index]) return true;
        if (Depth[index] < OpenWaterDepthMeters) return false;
        var concentratedFlow = IsConcentratedFlow(index);
        if (!concentratedFlow) return true;
        // A fast inlet may occupy a boundary cell of a standing lake. Keep that
        // cell in the lake whenever it touches a deep, non-channel neighbour;
        // otherwise the local three-neighbour test cuts square notches from the
        // shore and flips the apparent rounding direction.
        Span<int> neighbors = stackalloc int[4];
        var count = CardinalNeighborIndices(index, neighbors);
        var broadNeighbors = 0;
        for (var n = 0; n < count; n++)
        {
            var neighbor = neighbors[n];
            if (Ocean[neighbor]) return true;
            if (Depth[neighbor] < OpenWaterDepthMeters) continue;
            broadNeighbors++;
            if (!IsConcentratedFlow(neighbor)) return true;
        }
        // A one-cell channel has at most an upstream and downstream neighbour.
        return broadNeighbors >= 3;
    }

    private bool IsConcentratedFlow(int index) =>
        SmoothedDischarge[index] >= MinimumChannelDischargeCubicMetersPerDay;

    private static float RiverWidthMeters(float dischargeCubicMetersPerDay) =>
        1.2f + .18f * MathF.Sqrt(Math.Max(0, dischargeCubicMetersPerDay));

    private static DynamicRiverClass RiverClass(float dischargeCubicMetersPerDay, DynamicRiverClass current)
    {
        var width = RiverWidthMeters(dischargeCubicMetersPerDay);
        return current switch
        {
            DynamicRiverClass.Small when dischargeCubicMetersPerDay < 300 => DynamicRiverClass.None,
            DynamicRiverClass.Small when width >= 12 => DynamicRiverClass.Medium,
            DynamicRiverClass.Small => DynamicRiverClass.Small,
            DynamicRiverClass.Medium when width < 10 => DynamicRiverClass.Small,
            DynamicRiverClass.Medium when width >= 50 => DynamicRiverClass.Major,
            DynamicRiverClass.Medium => DynamicRiverClass.Medium,
            DynamicRiverClass.Major when width < 42 => DynamicRiverClass.Medium,
            DynamicRiverClass.Major => DynamicRiverClass.Major,
            _ when dischargeCubicMetersPerDay < MinimumChannelDischargeCubicMetersPerDay => DynamicRiverClass.None,
            _ when width >= 50 => DynamicRiverClass.Major,
            _ when width >= 12 => DynamicRiverClass.Medium,
            _ => DynamicRiverClass.Small
        };
    }

    public SurfaceWaterTerrainUpdate ApplyTerrainChanges(IReadOnlyDictionary<CellAddress, int> deltaCentimeters)
    {
        ArgumentNullException.ThrowIfNull(deltaCentimeters);
        if (deltaCentimeters.Count == 0)
            return new(Revision, 0, 0, 0, 0, 0, []);

        var inlandBefore = InlandVolume();
        var oceanBefore = OceanVolume();
        var previousOcean = Ocean;
        var changedWaterIndices = deltaCentimeters.Keys.Select(Index).ToHashSet();
        Span<int> groundNeighbors = stackalloc int[4];
        foreach (var (cell, delta) in deltaCentimeters)
        {
            if (!topology.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(deltaCentimeters));
            var index = Index(cell);
            Elevation[index] += delta / 100f;
            activeGroundwater.Add(index);
            var count = CardinalNeighborIndices(index, groundNeighbors);
            for (var n = 0; n < count; n++) activeGroundwater.Add(groundNeighbors[n]);
        }

        Ocean = BuildOceanMask();
        var added = 0; var removed = 0;
        for (var index = 0; index < Depth.Length; index++)
        {
            if (Ocean[index])
            {
                if (!previousOcean[index]) { added++; changedWaterIndices.Add(index); }
                Depth[index] = Math.Max(0, SeaLevel - Elevation[index]);
            }
            else if (previousOcean[index])
            {
                removed++; changedWaterIndices.Add(index);
                Depth[index] = 0;
            }
            // Existing inland depth is intentionally unchanged here: with a
            // fixed cell area that preserves its volume until the flow solver
            // has time to redistribute it.
        }
        RebuildShore();
        // A dry texel stores the signed distance to an adjacent water level, so
        // every changed wet/dry cell also invalidates its cardinal shore halo.
        Span<int> neighbors = stackalloc int[8];
        foreach (var index in changedWaterIndices.ToArray())
        {
            var neighborCount = ShoreNeighborIndices(index, neighbors);
            for (var n = 0; n < neighborCount; n++) changedWaterIndices.Add(neighbors[n]);
        }
        Revision++;
        return new(Revision, deltaCentimeters.Count, added, removed,
            OceanVolume() - oceanBefore, InlandVolume() - inlandBefore,
            changedWaterIndices.Select(Address).ToArray());
    }

    private bool[] BuildOceanMask()
    {
        var count = Elevation.Length;
        var labels = new int[count];
        Array.Fill(labels, -1);
        var queue = new int[count];
        var component = 0; var largestComponent = -1; var largestCount = 0;
        Span<int> neighbors = stackalloc int[4];

        for (var start = 0; start < count; start++)
        {
            if (Elevation[start] > SeaLevel || labels[start] >= 0) continue;
            var head = 0; var tail = 0; var componentCount = 0;
            labels[start] = component; queue[tail++] = start;
            while (head < tail)
            {
                var current = queue[head++]; componentCount++;
                var neighborCount = CardinalNeighborIndices(current, neighbors);
                for (var n = 0; n < neighborCount; n++)
                {
                    var next = neighbors[n];
                    if (Elevation[next] > SeaLevel || labels[next] >= 0) continue;
                    labels[next] = component; queue[tail++] = next;
                }
            }
            if (componentCount > largestCount)
            {
                largestCount = componentCount;
                largestComponent = component;
            }
            component++;
        }

        var result = new bool[count];
        if (largestComponent >= 0)
            for (var index = 0; index < count; index++) result[index] = labels[index] == largestComponent;
        return result;
    }

    private void RebuildShore()
    {
        RebuildShore(Enumerable.Range(0, Shore.Length));
    }

    private void RebuildShore(IEnumerable<int> indices)
    {
        Span<int> neighbors = stackalloc int[8];
        foreach (var index in indices)
        {
            if (IsOpenWater(index)) { Shore[index] = Math.Max(.01f, Depth[index]); continue; }
            var level = float.NegativeInfinity;
            // A shoreline is the zero contour of (local water level - terrain),
            // not a rounded binary cell mask. The diagonal sample is essential:
            // its signed value lets bilinear interpolation resolve a saddle by
            // slope, keeping the curve centred on the lower corner. A -64
            // sentinel here produced the inverted square hooks at concave bays.
            var neighborCount = ShoreNeighborIndices(index, neighbors);
            for (var n = 0; n < neighborCount; n++)
            {
                var neighbor = neighbors[n];
                if (IsOpenWater(neighbor)) level = Math.Max(level, Elevation[neighbor] + Depth[neighbor]);
            }
            Shore[index] = float.IsFinite(level) ? Math.Min(-.01f, level - Elevation[index]) : -64;
        }
    }

    private int WeatherIndex(int index, int weatherResolution)
    {
        var face = index / faceLength;
        var local = index % faceLength;
        var x = local % Resolution;
        var y = local / Resolution;
        return (face * weatherResolution + Math.Min(weatherResolution - 1, y * weatherResolution / Resolution)) * weatherResolution +
            Math.Min(weatherResolution - 1, x * weatherResolution / Resolution);
    }

    private float GroundwaterHead(int index) =>
        AquiferBase[index] + GroundwaterStorage[index] / GroundwaterSpecificYield;

    private int CardinalNeighborIndices(int index, Span<int> result)
    {
        var x = index % Resolution; var y = index / Resolution % Resolution;
        if (x > 0 && y > 0 && x < Resolution - 1 && y < Resolution - 1)
        {
            result[0] = index - Resolution;
            result[1] = index - 1;
            result[2] = index + 1;
            result[3] = index + Resolution;
            return 4;
        }
        var count = 0;
        foreach (var cell in topology.GetNeighbors(Address(index))) result[count++] = Index(cell);
        return count;
    }

    private int ShoreNeighborIndices(int index, Span<int> result)
    {
        var x = index % Resolution; var y = index / Resolution % Resolution;
        if (x > 0 && y > 0 && x < Resolution - 1 && y < Resolution - 1)
        {
            result[0] = index - Resolution - 1;
            result[1] = index - Resolution;
            result[2] = index - Resolution + 1;
            result[3] = index - 1;
            result[4] = index + 1;
            result[5] = index + Resolution - 1;
            result[6] = index + Resolution;
            result[7] = index + Resolution + 1;
            return 8;
        }

        var cell = Address(index);
        var neighbors = new HashSet<CellAddress>(topology.GetNeighbors(cell));
        foreach (var dx in new[] { -1, 1 })
            foreach (var dy in new[] { -1, 1 })
                neighbors.Add(topology.Locate(CubeSphereTopology.ProjectFacePoint(cell.Face,
                    -1 + (cell.X + dx + .5) * 2 / Resolution,
                    -1 + (cell.Y + dy + .5) * 2 / Resolution)));
        neighbors.Remove(cell);
        var count = 0;
        foreach (var neighbor in neighbors.OrderBy(Index)) result[count++] = Index(neighbor);
        return count;
    }

    private double OceanVolume()
    {
        double result = 0;
        for (var index = 0; index < Depth.Length; index++) if (Ocean[index]) result += Depth[index] * cellAreaSquareMeters;
        return result;
    }

    private double InlandVolume()
    {
        double result = 0;
        for (var index = 0; index < Depth.Length; index++) if (!Ocean[index]) result += Depth[index] * cellAreaSquareMeters;
        return result;
    }

    private double InlandStorageVolume()
    {
        double result = 0;
        for (var index = 0; index < Depth.Length; index++)
            if (!Ocean[index]) result += (Depth[index] + SoilWater[index] + GroundwaterStorage[index]) * cellAreaSquareMeters;
        return result;
    }
}
