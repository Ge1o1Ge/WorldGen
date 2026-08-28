using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class NeedsAndDemand
{
    private static readonly Dictionary<string, double> MonthlyInfrastructurePerPerson = new(StringComparer.Ordinal)
    {
        ["timber"] = 0.001, ["clay"] = 0.0007, ["stone"] = 0.0005, ["tools"] = 0.00004
    };

    public static double SeasonalNeedFactor(WorldState world, SeasonalityDefinition? seasonality)
    {
        if (seasonality is null) return 1;
        var dayOfYear = world.Day % world.Calendar.DaysPerYear;
        var wave = 0.5 + 0.5 * Math.Cos(Math.PI * 2 * (dayOfYear - seasonality.PeakDay) / world.Calendar.DaysPerYear);
        return seasonality.Minimum + (1 - seasonality.Minimum) * wave;
    }

    public static double DailyHouseholdNeed(WorldState world, CityState city, ResourceDefinition resource)
    {
        if (resource.HouseholdNeed is null) return 0;
        var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
        var perPerson = resource.Id == "food" ? city.FoodPerPersonPerDay : resource.HouseholdNeed.PerPersonPerDay;
        if (resource.Id == "firewood" && world.SettlementDevelopment?.Cities.GetValueOrDefault(city.Id)?.Primitive?.Weather is { } weather)
            return population * perPerson * Math.Clamp(.25 + (18 - weather.TemperatureC) / 20, .25, 1.6);
        return population * perPerson * SeasonalNeedFactor(world, resource.HouseholdNeed.Seasonality);
    }

    public static double InfrastructureMonthlyNeed(CityState city, int population, string resourceId) =>
        population * MonthlyInfrastructurePerPerson.GetValueOrDefault(resourceId) *
        (1.15 - city.Infrastructure.HousingCondition * 0.25);

    public static IEnumerable<string> InfrastructureResourceIds => MonthlyInfrastructurePerPerson.Keys.Order(StringComparer.Ordinal);

    public static double DailyResourceNeed(WorldState world, CityState city, string resourceId, ContentCatalog content)
    {
        var resource = content.Resources.Resources.First(item => item.Id == resourceId);
        var daily = DailyHouseholdNeed(world, city, resource);
        var recipes = content.Recipes.Recipes.ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        foreach (var industry in city.Industries)
            daily += recipes[industry.RecipeId].Inputs.GetValueOrDefault(resourceId) * industry.Capacity;
        var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
        return daily + InfrastructureMonthlyNeed(city, population, resourceId) / 30;
    }

    public static double ResourceTargetStock(WorldState world, CityState city, string resourceId, ContentCatalog content)
    {
        var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
        var monthly = InfrastructureMonthlyNeed(city, population, resourceId);
        return DailyResourceNeed(world, city, resourceId, content) * city.LocalReserveDays +
            monthly * Math.Max(0, 1.2 - city.LocalReserveDays / 30d);
    }
}
