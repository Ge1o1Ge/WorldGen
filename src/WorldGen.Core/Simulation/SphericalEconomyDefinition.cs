using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public sealed record SphericalEconomyDefinition(int SchemaVersion, string Id, string Name,
    IReadOnlyList<SphericalCityEconomyDefinition> Cities, IReadOnlyList<RouteDefinition> Routes)
{
    public string Stage { get; init; } = "early_iron";
}
public sealed record SphericalCityEconomyDefinition(string SettlementId, IReadOnlyList<string> SourceCities, int Population)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? InitialTechnologies { get; init; }
}
