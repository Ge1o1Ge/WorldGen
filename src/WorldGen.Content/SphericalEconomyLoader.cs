using System.Text.Json;
using System.Text.Json.Serialization;
using WorldGen.Core.Simulation;

namespace WorldGen.Content;

public static class SphericalEconomyLoader
{
    public static async Task<SphericalEconomyDefinition> LoadAsync(string? contentDirectory = null, string scenario = "early_iron")
    {
        var file = scenario switch { "early_iron" => "spherical-economy.json", "foragers" => "spherical-foragers.json", "primordial" => "spherical-primordial.json", _ => throw new ArgumentException("Неизвестный сферический сценарий", nameof(scenario)) };
        var path = Path.Combine(contentDirectory ?? ContentLoader.FindContentDirectory(), "worlds", file);
        var result = JsonSerializer.Deserialize<SphericalEconomyDefinition>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("Пустой сферический сценарий");
        if (result.SchemaVersion != 1 || result.Stage is not ("foragers" or "early_iron") || result.Cities.Count == 0 || result.Cities.Any(city => city.Population < 1 || city.SourceCities.Count == 0))
            throw new InvalidOperationException("Некорректный сферический сценарий");
        return result;
    }
}
