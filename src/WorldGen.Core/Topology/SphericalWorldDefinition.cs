namespace WorldGen.Core.Topology;

public sealed record SphericalWorldDefinition
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Topology { get; init; }
    public required int FaceSize { get; init; }
    public required int ChunkSize { get; init; }
    public required double ZoneSizeMeters { get; init; }
    public required uint Seed { get; init; }
    public required SphericalTerrainSettings Terrain { get; init; }
    public required SphericalClimateSettings Climate { get; init; }
    public required IReadOnlyList<SphericalSettlementDefinition> Settlements { get; init; }

    public long ZoneCount => 6L * FaceSize * FaceSize;
    public long TriangleCount => ZoneCount * 2;
    public double NominalSurfaceAreaSquareKilometers => ZoneCount * ZoneSizeMeters * ZoneSizeMeters / 1_000_000;
}

public sealed record SphericalTerrainSettings
{
    public required double SeaLevelMeters { get; init; }
    public required double ElevationBaseMeters { get; init; }
    public required double ElevationRangeMeters { get; init; }
    public required double Roughness { get; init; }
    public required double ForestThreshold { get; init; }
}

public sealed record SphericalClimateSettings
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? LapseRatePerMeter { get; init; }
    public required double EquatorTemperatureC { get; init; }
    public required double PoleTemperatureC { get; init; }
    public required double MoistureBase { get; init; }
}

public sealed record SphericalSettlementDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<SphericalBuildingDefinition> Buildings { get; init; }
    public required IReadOnlyList<SphericalLandUseDefinition> UsedLands { get; init; }
}

public sealed record SphericalBuildingDefinition
{
    public required string Id { get; init; }
    public required string BuildingTypeId { get; init; }
    public required float InfluenceStrength { get; init; }
    public required IReadOnlyList<SphericalCapacityAllocation> Footprint { get; init; }
}

public sealed record SphericalCapacityAllocation
{
    public required CubeFace Face { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int CapacityUnits { get; init; }
}

public sealed record SphericalLandUseDefinition
{
    public required string Id { get; init; }
    public required CubeFace Face { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required string Kind { get; init; }
    public required float Usage { get; init; }
    public required float InfluenceStrength { get; init; }
}
