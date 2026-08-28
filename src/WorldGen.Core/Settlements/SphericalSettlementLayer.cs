using WorldGen.Core.Topology;

namespace WorldGen.Core.Settlements;

public sealed class SphericalSettlementLayer
{
    private readonly CubeSphereTopology topology;
    private readonly SphericalTerrainGenerator terrain;
    private readonly List<UsedLandParcel> lands;
    private readonly Dictionary<CellAddress, float> traversalCache = new();
    private CityInfluenceMap? influence;
    private long builtConstructionRevision = -1;
    private long revision;
    private bool dirty = true;

    private SphericalSettlementLayer(
        ConstructionRegistry construction,
        List<UsedLandParcel> usedLands,
        CubeSphereTopology topology,
        SphericalTerrainGenerator terrain)
    {
        Construction = construction;
        lands = usedLands;
        UsedLands = lands.AsReadOnly();
        this.topology = topology;
        this.terrain = terrain;
    }

    public ConstructionRegistry Construction { get; }
    public IReadOnlyList<UsedLandParcel> UsedLands { get; }
    public CityInfluenceMap Influence { get { RebuildInfluence(); return influence!; } }
    public long Revision { get { RebuildInfluence(); return revision; } }

    public bool SetLandUsage(string id, float usage)
    {
        if (!float.IsFinite(usage) || usage is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(usage));
        var index = lands.FindIndex(land => land.Id == id);
        if (index < 0) return false;
        if (lands[index].Usage == usage) return true;
        lands[index] = lands[index] with { Usage = usage };
        dirty = true;
        return true;
    }

    public void UpsertLand(UsedLandParcel land)
    {
        var index=lands.FindIndex(p=>p.Id==land.Id);
        if(index>=0&&lands[index]==land)return;
        if(index<0)lands.Add(land);else lands[index]=land;
        dirty=true;
    }

    private void RebuildInfluence()
    {
        if (!dirty && builtConstructionRevision == Construction.Revision) return;
        var sources = Construction.ToInfluenceSources().Concat(lands.Select(land => land.ToInfluenceSource()))
            .Where(source => source.Strength >= 0.045f).ToArray();
        influence = CityInfluenceEngine.Build(topology, sources, TraversalCost,
            new CityInfluenceSettings(FalloffPerTravelCost: 0.032f, MinimumClaim: 0.045f));
        dirty = false;
        builtConstructionRevision = Construction.Revision;
        revision++;
    }

    private float TraversalCost(CellAddress cell)
    {
        if (!traversalCache.TryGetValue(cell, out var value))
        {
            value = terrain.GenerateCell(cell).TraversalCost;
            traversalCache.Add(cell, value);
        }
        return value;
    }

    public static SphericalSettlementLayer Build(
        SphericalWorldDefinition definition,
        CubeSphereTopology topology,
        SphericalTerrainGenerator terrain)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(terrain);
        var construction = new ConstructionRegistry(topology);
        var lands = new List<UsedLandParcel>();
        foreach (var settlement in definition.Settlements.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var building in settlement.Buildings.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                construction.Place(new BuildingPlacement(
                    building.Id,
                    settlement.Id,
                    building.BuildingTypeId,
                    building.Footprint.Select(item => new CellCapacityAllocation(
                        new CellAddress(item.Face, item.X, item.Y), item.CapacityUnits)).ToArray(),
                    building.InfluenceStrength));
            }
            foreach (var land in settlement.UsedLands.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                lands.Add(new UsedLandParcel(
                    land.Id,
                    settlement.Id,
                    new CellAddress(land.Face, land.X, land.Y),
                    ParseKind(land.Kind),
                    land.Usage,
                    land.InfluenceStrength));
            }
        }

        return new SphericalSettlementLayer(construction, lands, topology, terrain);
    }

    private static CityAssetKind ParseKind(string kind) => kind switch
    {
        "cultivated_field" => CityAssetKind.CultivatedField,
        "pasture" => CityAssetKind.Pasture,
        "orchard" => CityAssetKind.Orchard,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Неизвестный вид угодья")
    };
}
