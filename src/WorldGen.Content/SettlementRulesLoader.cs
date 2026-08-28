using System.Text.Json;
using System.Text.Json.Serialization;
using WorldGen.Core.Simulation;

namespace WorldGen.Content;

public static class SettlementRulesLoader
{
    public static async Task<SettlementRules> LoadAsync(string? contentDirectory = null, string scenario = "foragers")
    {
        var path = Path.Combine(contentDirectory ?? ContentLoader.FindContentDirectory(), "worlds", "settlement-rules.json");
        var rules = JsonSerializer.Deserialize<SettlementRules>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("Пустые правила поселений");
        if (scenario == "primordial")
        {
            var eraPath = Path.Combine(contentDirectory ?? ContentLoader.FindContentDirectory(), "worlds", "primordial-rules.json");
            var era = JsonSerializer.Deserialize<PrimitiveWorldRules>(await File.ReadAllTextAsync(eraPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
            var bioPath=Path.Combine(contentDirectory ?? ContentLoader.FindContentDirectory(),"worlds","biosphere.json");
            var bio=JsonSerializer.Deserialize<BiosphereRules>(await File.ReadAllTextAsync(bioPath),
                new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow })!;
            bio.Validate();
            era=era with { Biosphere=bio, Technologies=era.Technologies.Concat(bio.Technologies()).ToArray(), Resources=era.Resources.Concat(bio.Resources()).ToArray() };
            era.Validate();
            rules = rules with
            {
                Primitive = era,
                Lifecycle = rules.Lifecycle is { } lifecycle ? lifecycle with
                {
                    Materials = lifecycle.Materials.Concat(era.Materials).ToArray()
                } : null,
                Resources = rules.Resources.Select(r => r.Id == "cloth" ? r with { HouseholdNeed = null } : r).Concat(era.Resources).ToArray(),
                Activities = rules.Activities.Concat(era.Activities).ToArray(),
                Discoveries = era.Technologies.Select(t => new SettlementDiscoveryRule(t.Id, t.Name, t.Practice, t.PracticeHours)).ToArray()
            };
        }
        rules.Validate();
        return rules;
    }
}
