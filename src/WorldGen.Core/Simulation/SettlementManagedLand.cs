using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private double RunManagedLandSites(CityState city, double available, DailyTelemetry telemetry)
    {
        if (available <= 0 || Rules.Primitive is null) return 0;
        var life = State.Cities[city.Id];
        var spent = 0d;
        foreach (var lodge in State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "forester_lodge" && b.Status == "active"))
        {
            var budget = Math.Min(2, available - spent);
            if (budget <= 0) break;
            var route = Routes(lodge.Cell);
            var managed = route.Cost.Where(pair => pair.Value <= 4 && !OpenWater(pair.Key) &&
                    terrain[pair.Key].ForestCover > .15 && !State.Buildings.Any(b => b.Cell == pair.Key && b.Kind == "garden" && Standing(b)))
                .OrderBy(pair => terrain[pair.Key].NaturalState.ForestBiomass)
                .ThenBy(pair => pair.Value).ThenBy(pair => SphericalSimulation.ZoneId(pair.Key), StringComparer.Ordinal)
                .Take(8).ToArray();
            if (managed.Length == 0) continue;
            var hours = budget / managed.Length;
            foreach (var site in managed)
            {
                var natural = terrain[site.Key].NaturalState;
                natural.ManagedForestCare = Math.Min(1, natural.ManagedForestCare + hours * .18);
            }
            spent += budget;
            Add(life.PracticeHours, "wood", budget);
            life.Tasks.Add(new(lodge.Id, "forest_management", managed[0].Key, budget, managed.Sum(site => terrain[site.Key].NaturalState.ManagedForestCare)));
            Passage(route.Path(managed[0].Key), .25);
        }

        foreach (var quarry in State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "quarry" && b.Status == "active"))
        {
            var missing = Math.Max(0, Math.Max(2, Population(city) * .025) - city.Stocks.GetValueOrDefault("stone"));
            if (missing <= 0 || available - spent <= 0) continue;
            var site = terrain[quarry.Cell];
            var difficulty = ExtractionDifficulty(site, "stone");
            var hours = Math.Min(available - spent, Math.Min(life.LaborAvailableHours * .08, missing / Math.Max(1e-9, .004 / difficulty)));
            var stone = Extract(site, "stone", hours * .004 / difficulty);
            if (stone <= 0) continue;
            city.Stocks["stone"] += stone;
            Add(telemetry.ProductionByResource, "stone", stone);
            Add(life.Production, "stone", stone);
            Add(life.PracticeHours, "stone", hours);
            // Quarry waste occasionally exposes a small ore-bearing vein. The
            // ore still has its own zonal grade and rapidly increasing depth cost.
            var oreGrade = site.ResourcePotential.GetValueOrDefault("iron_ore");
            var ore = Knows(city, "bloomery_smelting") && oreGrade > .02
                ? Extract(site, "iron_ore", stone * oreGrade * .025 / ExtractionDifficulty(site, "iron_ore")) : 0;
            if (ore > 0)
            {
                city.Stocks["iron_ore"] += ore;
                Add(telemetry.ProductionByResource, "iron_ore", ore);
                Add(life.Production, "iron_ore", ore);
            }
            spent += hours;
            life.Tasks.Add(new(quarry.Id, "quarry", quarry.Cell, hours, stone + ore));
        }
        return spent;
    }
}
