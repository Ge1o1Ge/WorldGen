using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public static class Demography
{
    public static void Advance(WorldState world)
    {
        if (world.Day == 0 || world.Day % 30 != 0) return;
        var changes = world.Cities.Keys.Order(StringComparer.Ordinal)
            .ToDictionary(id => id, id => MonthlyChange(world, world.Cities[id]), StringComparer.Ordinal);
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId];
            if (!city.Shortage.Active || city.Shortage.EpisodeDays < 10) continue;
            var destination = MigrationDestination(world, cityId); if (destination is null) continue;
            var change = changes[cityId];
            var migrants = Math.Min(change.Target, Math.Max(1, (int)Math.Floor(change.Population * world.DemographyPolicy.MonthlyMigrationShare)));
            change.Target -= migrants; changes[destination].Target += migrants;
            city.Demography.Emigration += migrants; world.Cities[destination].Demography.Immigration += migrants;
            Journal.Record(world, "migration_flow", cityId, city.Shortage.EventId is null ? [] : [city.Shortage.EventId],
                new JsonObject { ["from"] = cityId, ["to"] = destination, ["people"] = migrants });
        }
        foreach (var pair in changes) SetCityPopulation(world, pair.Key, pair.Value.Target);
        SpatialRuntime.RecalculateAggregates(world);
        if (world.Day % world.Calendar.DaysPerYear < 30)
        foreach (var pair in changes)
            Journal.Record(world, "population_report", pair.Key, details:
                new JsonObject { ["cityId"] = pair.Key, ["population"] = pair.Value.Target,
                    ["birthsToDate"] = world.Cities[pair.Key].Demography.Births, ["deathsToDate"] = world.Cities[pair.Key].Demography.Deaths });
    }

    private static VitalChange MonthlyChange(WorldState world, CityState city)
    {
        var policy = world.DemographyPolicy; var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
        var birthExpected = population * policy.BirthRatePerYear * 30 / world.Calendar.DaysPerYear + city.Demography.BirthRemainder;
        var mortality = (1.22 - city.Demography.Health * 0.28) * (city.Shortage.Active ? policy.ShortageMortalityMultiplier : 1) *
            (city.Needs.Values.Any(need => need.Active) ? 1.18 : 1);
        var deathExpected = population * policy.DeathRatePerYear * mortality * 30 / world.Calendar.DaysPerYear + city.Demography.DeathRemainder;
        var births = (int)Math.Floor(birthExpected); var deaths = Math.Min(population + births, (int)Math.Floor(deathExpected));
        city.Demography.BirthRemainder = birthExpected - births; city.Demography.DeathRemainder = deathExpected - deaths;
        city.Demography.Births += births; city.Demography.Deaths += deaths;
        var infrastructureHealth = (city.Infrastructure.HousingCondition + city.Infrastructure.Sanitation) / 2;
        city.Demography.Health = SimulationMath.Clamp(city.Demography.Health + (infrastructureHealth - city.Demography.Health) * 0.035 +
            (city.Shortage.Active ? -0.025 : city.Needs.Values.Any(need => need.Active) ? -0.006 : 0.002), 0.1, 1);
        return new VitalChange(population, population + births - deaths);
    }

    private static string? MigrationDestination(WorldState world, string sourceId) => world.Routes
        .Where(route => route.A == sourceId || route.B == sourceId).Select(route => route.A == sourceId ? route.B : route.A)
        .Where(id => !world.Cities[id].Shortage.Active)
        .OrderByDescending(id =>
        {
            var city = world.Cities[id]; var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            return city.Stocks["food"] / Math.Max(0.001, population * city.FoodPerPersonPerDay);
        }).ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();

    private static void SetCityPopulation(WorldState world, string cityId, int target)
    {
        var node = world.Spatial.Nodes[$"city:{cityId}"]; var territories = node.ChildTerritoryIds!.Select(id => world.Spatial.Territories[id]).ToArray();
        var current = territories.Sum(item => item.Population);
        var weights = territories.Select(item => Math.Max(0.1, item.Population + (item.Id == node.AnchorTerritoryId ? Math.Max(10, current * 0.01) : 0))).ToArray();
        var totalWeight = weights.Sum(); var assigned = 0; var fractions = new List<(Spatial.Territory Territory, double Fraction)>();
        for (var i = 0; i < territories.Length; i++)
        {
            var exact = target * weights[i] / totalWeight; territories[i].Population = (int)Math.Floor(exact);
            assigned += territories[i].Population; fractions.Add((territories[i], exact - Math.Floor(exact)));
        }
        fractions.Sort((a, b) => { var c = b.Fraction.CompareTo(a.Fraction); return c != 0 ? c : SimulationMath.LocaleComparer.Compare(a.Territory.Id, b.Territory.Id); });
        for (var i = 0; i < target - assigned; i++) fractions[i].Territory.Population++;
    }

    private sealed record VitalChange(int Population, int InitialTarget) { public int Target { get; set; } = InitialTarget; }
}
