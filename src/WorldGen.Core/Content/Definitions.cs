using System.Text.Json.Nodes;

namespace WorldGen.Core.Content;

public sealed record ContentCatalog(
    ResourceDocument Resources,
    RecipeDocument Recipes,
    TechnologyDocument Technologies,
    MapDocument Map,
    ScenarioDocument Scenario,
    JsonObject Raw,
    string Fingerprint);

public sealed record ResourceDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<ResourceDefinition> Resources { get; init; }
}

public sealed record ResourceDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required string Category { get; init; }
    public required double BaseValue { get; init; }
    public required double DecayPerDay { get; init; }
    /// <summary>Food-equivalent tonnes supplied by one stored unit; zero means inedible.</summary>
    public double FoodValue { get; init; }
    public HouseholdNeedDefinition? HouseholdNeed { get; init; }
}

public sealed record HouseholdNeedDefinition
{
    public required double PerPersonPerDay { get; init; }
    public SeasonalityDefinition? Seasonality { get; init; }
}

public sealed record SeasonalityDefinition
{
    public required double Minimum { get; init; }
    public required int PeakDay { get; init; }
}

public sealed record RecipeDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<RecipeDefinition> Recipes { get; init; }
}

public sealed record RecipeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required IReadOnlyDictionary<string, double> Inputs { get; init; }
    public required IReadOnlyDictionary<string, double> Outputs { get; init; }
    public required double LaborPerBatch { get; init; }
    public string? SitePotential { get; init; }
    public required IReadOnlyList<string> RequiredTechnologyIds { get; init; }
    public SeasonalityDefinition? Seasonality { get; init; }
}

public sealed record TechnologyDocument
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<TechnologyDefinition> Technologies { get; init; }
    public required IReadOnlyList<TechnologyRelation> Relations { get; init; }
}

public sealed record TechnologyDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required double Complexity { get; init; }
    public required double Diffusion { get; init; }
}

public sealed record TechnologyRelation
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Type { get; init; }
}

public sealed record MapDocument
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int GeneratorVersion { get; init; }
    public required GridDefinition Grid { get; init; }
    public required PopulationDefinition Population { get; init; }
    public required TerrainDefinition Terrain { get; init; }
    public required ClimateDefinition Climate { get; init; }
    public required HydrologyDefinition Hydrology { get; init; }
}

public sealed record GridDefinition
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double ZoneSizeMeters { get; init; }
    public required int AggregationFactor { get; init; }
    public required double VertexJitter { get; init; }
    public required uint Seed { get; init; }
}

public sealed record PopulationDefinition
{
    public required int Total { get; init; }
    public required double UrbanConcentration { get; init; }
    public required double UrbanRadius { get; init; }
}

public sealed record TerrainDefinition
{
    public required double FertilityBase { get; init; }
    public required double FertilityVariation { get; init; }
    public required double ElevationBaseMeters { get; init; }
    public required double ElevationRangeMeters { get; init; }
    public required double Roughness { get; init; }
}

public sealed record ClimateDefinition
{
    public required double MeanTemperatureC { get; init; }
    public required double TemperatureRangeC { get; init; }
    public required double Rainfall { get; init; }
}

public sealed record HydrologyDefinition
{
    public required double RiverCenterY { get; init; }
    public required double RiverWidthZones { get; init; }
    public required double FloodplainWidthZones { get; init; }
    public required double Meander { get; init; }
}

public sealed record ScenarioDocument
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MapFile { get; init; }
    public required uint Seed { get; init; }
    public required CalendarDefinition Calendar { get; init; }
    public required int ReserveDays { get; init; }
    public required DemographyDefinition Demography { get; init; }
    public required LodPolicyDefinition LodPolicy { get; init; }
    public required IReadOnlyList<CityDefinition> Cities { get; init; }
    public required IReadOnlyList<ImportantActorDefinition> ImportantActors { get; init; }
    public required IReadOnlyList<RouteDefinition> Routes { get; init; }
    public required IReadOnlyList<ScheduledEventDefinition> ScheduledEvents { get; init; }
}

public sealed record CalendarDefinition
{
    public required int DaysPerYear { get; init; }
    public required int StartYear { get; init; }
}

public sealed record DemographyDefinition
{
    public required double BirthRatePerYear { get; init; }
    public required double DeathRatePerYear { get; init; }
    public required double ShortageMortalityMultiplier { get; init; }
    public required double MonthlyMigrationShare { get; init; }
}

public sealed record LodPolicyDefinition
{
    public required int CrisisCooldownDays { get; init; }
    public required int ShortageCooldownDays { get; init; }
}

public sealed record GridCoordinate
{
    public required int X { get; init; }
    public required int Y { get; init; }
}

public sealed record CityDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required GridCoordinate Anchor { get; init; }
    public required double WorkerShare { get; init; }
    public required double FoodPerPersonPerDay { get; init; }
    public required IReadOnlyDictionary<string, double> Stocks { get; init; }
    public required IReadOnlyList<IndustryDefinition> Industries { get; init; }
    public required IReadOnlyList<InstitutionDefinition> Institutions { get; init; }
    public required IReadOnlyDictionary<string, double[]> TechnologySeeds { get; init; }
}

public sealed record IndustryDefinition
{
    public required string Id { get; init; }
    public required string RecipeId { get; init; }
    public required double Capacity { get; init; }
    public required GridCoordinate Zone { get; init; }
}

public sealed record InstitutionDefinition
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required double Competence { get; init; }
    public required double LearningRate { get; init; }
    public required IReadOnlyList<string> Priorities { get; init; }
}

public sealed record ImportantActorDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required GridCoordinate Zone { get; init; }
    public required double Importance { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}

public sealed record RouteDefinition
{
    public required string Id { get; init; }
    public required string A { get; init; }
    public required string B { get; init; }
    public required int TravelDays { get; init; }
    public required double DailyCapacity { get; init; }
}

public sealed record ScheduledEventDefinition
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string CityId { get; init; }
    public required int StartDay { get; init; }
    public required int DurationDays { get; init; }
    public required double Multiplier { get; init; }
    public required string Label { get; init; }
}
