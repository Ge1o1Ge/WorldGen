namespace WorldGen.Core.Spatial;

public sealed record SpatialHierarchy
{
    public required string RegionNodeId { get; init; }
    public required SpatialGrid Grid { get; init; }
    public required Dictionary<string, Territory> Territories { get; init; }
    public required Dictionary<string, SpatialNode> Nodes { get; init; }
}

public sealed record SpatialGrid
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double ZoneSizeMeters { get; init; }
    public required int AggregationFactor { get; init; }
    public required int MacroWidth { get; init; }
    public required int MacroHeight { get; init; }
    public required double VertexJitter { get; init; }
    public required uint Seed { get; init; }
    public required int GeneratorVersion { get; init; }
    public required IReadOnlyList<SpatialLevel> Levels { get; init; }
}

public sealed record SpatialLevel(int Level, string Kind, int Width, int Height, int Scale);

public sealed class Territory
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required GridPosition Grid { get; init; }
    public required double Area { get; init; }
    public int Population { get; set; }
    public required double ElevationMeters { get; init; }
    public required double Slope { get; init; }
    public required double TemperatureC { get; init; }
    public required double Moisture { get; init; }
    public required double Fertility { get; init; }
    public required double ForestCover { get; init; }
    public required string Biome { get; init; }
    public required string Terrain { get; init; }
    public required WaterState Water { get; init; }
    public required IReadOnlyDictionary<string, double> ResourcePotential { get; init; }
    public required string AssignedCityId { get; set; }
    public required string ParentNodeId { get; init; }
    public required IReadOnlyList<string> TriangleIds { get; init; }
    public required string Diagonal { get; init; }
    public required NaturalState NaturalState { get; init; }
}

public sealed record GridPosition(int X, int Y);

public sealed record WaterState(bool River, bool Floodplain, double DistanceToRiver);

public sealed record NaturalState
{
    public required double SoilQuality { get; set; }
    public required double ForestBiomass { get; set; }
    public required double FishStock { get; set; }
    public required Dictionary<string, double> Deposits { get; init; }
    public required Dictionary<string, double> ExtractedBatches { get; init; }
    public SoilProfileState Soil { get; init; } = new();
    public double ManagedForestCare { get; set; }
}

public sealed class SoilProfileState
{
    public double Nutrients { get; set; } = 1;
    public double OrganicMatter { get; set; } = .5;
    public double Rockiness { get; set; }
    public double MoistureRetention { get; set; } = .5;
    public double Pests { get; set; }
    public double Compaction { get; set; }
    public double GrazingBiomass { get; set; } = 1;
    public int LastGrazedDay { get; set; } = -10000;
    public Dictionary<string, double> Pathogens { get; init; } = new(StringComparer.Ordinal);
}

public sealed record SpatialNode
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public GridPosition? Grid { get; init; }
    public string? Projection { get; init; }
    public string? WorldEntityId { get; init; }
    public string? Name { get; init; }
    public string? ParentNodeId { get; init; }
    public IReadOnlyList<string>? ChildTerritoryIds { get; init; }
    public IReadOnlyList<string>? ChildNodeIds { get; init; }
    public IReadOnlyList<string>? OverlayNodeIds { get; init; }
    public string? DominantCityId { get; set; }
    public string? AnchorTerritoryId { get; init; }
    public required SpatialAggregate Aggregate { get; set; }
    public SpatialDetail? Detail { get; set; }
    public int? ActiveUntilDay { get; set; }
}

public sealed record SpatialDetail
{
    public required int ExpandedDay { get; init; }
    public required List<string> TriggerEventIds { get; init; }
    public required int ZoneCount { get; init; }
    public required List<string> ActorIds { get; init; }
    public string? ExpansionEventId { get; set; }
}

public sealed record SpatialAggregate
{
    public required double Area { get; init; }
    public required int Population { get; init; }
    public required double Fertility { get; init; }
    public required double MeanElevationMeters { get; init; }
    public required double MeanMoisture { get; init; }
    public required IReadOnlyDictionary<string, double> ResourcePotential { get; init; }
    public required IReadOnlyDictionary<string, double> BiomeShares { get; init; }
}
