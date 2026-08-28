using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class Economy
{
    private const double Epsilon = 1e-9;

    public static DailyTelemetry RunDay(WorldState world, ContentCatalog content, IReadOnlyDictionary<string, double>? utilization = null, SettlementSimulation? development = null)
    {
        var telemetry = new DailyTelemetry { Day = world.Day };
        development?.ReconcileFoodComposition();
        DecayStocks(world, content, telemetry);
        development?.ReconcileFoodComposition();
        if (development is null) RecoverNaturalSites(world); else development.RecoverNaturalSites();
        development?.RunDay(telemetry);
        Produce(world, content, telemetry, utilization, development);
        ConsumeFood(world, telemetry, development);
        ConsumeOtherNeeds(world, content, telemetry, development);
        development?.EvaluateWellbeing();
        return telemetry;
    }

    private static void DecayStocks(WorldState world, ContentCatalog content, DailyTelemetry telemetry)
    {
        var resources = content.Resources.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        foreach (var city in world.Cities.Values)
        foreach (var resourceId in city.Stocks.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            var decay = resources[resourceId].DecayPerDay;
            if (decay <= 0 || city.Stocks[resourceId] <= 0) continue;
            var lost = SimulationMath.Quantize(city.Stocks[resourceId] * decay);
            city.Stocks[resourceId] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resourceId] - lost));
            telemetry.DecayedByResource[resourceId] = SimulationMath.Quantize(telemetry.DecayedByResource.GetValueOrDefault(resourceId) + lost);
        }
    }

    private static void RecoverNaturalSites(WorldState world)
    {
        var ids = world.Cities.Values.SelectMany(city => city.Industries.Select(industry => industry.ZoneId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Order(StringComparer.Ordinal))
        {
            var territory = world.Spatial.Territories[id];
            var natural = territory.NaturalState;
            natural.SoilQuality = SimulationMath.Quantize(natural.SoilQuality + (territory.Fertility - natural.SoilQuality) * 0.00045);
            natural.ForestBiomass = SimulationMath.Quantize(natural.ForestBiomass + (territory.ForestCover - natural.ForestBiomass) * 0.0003);
            natural.FishStock = SimulationMath.Quantize(natural.FishStock + (territory.ResourcePotential["fish"] - natural.FishStock) * 0.004);
        }
    }

    private static double TechnologyEfficiency(CityState city, RecipeDefinition recipe)
    {
        if (recipe.RequiredTechnologyIds.Count == 0) return 1;
        var practice = recipe.RequiredTechnologyIds.Select(id => city.TechnologyState[id])
            .Min(state => Math.Min(Math.Min(state.Knowledge, state.Competence), Math.Min(state.Capability, state.Adoption)));
        return 0.22 + practice * 0.78;
    }

    private static double SeasonalityFactor(WorldState world, RecipeDefinition recipe) =>
        NeedsAndDemand.SeasonalNeedFactor(world, recipe.Seasonality);

    private static double SiteFactor(WorldState world, IndustryState industry, RecipeDefinition recipe)
    {
        if (recipe.SitePotential is null) return 1;
        var territory = world.Spatial.Territories[industry.ZoneId];
        var potential = recipe.SitePotential;
        var basePotential = territory.ResourcePotential[potential];
        var ratio = 1d;
        if (potential is "arable" or "pasture") ratio = territory.Fertility > 0 ? Math.Max(0.15, territory.NaturalState.SoilQuality / territory.Fertility) : 0.15;
        else if (potential == "timber") ratio = territory.ForestCover > 0 ? Math.Max(0.05, territory.NaturalState.ForestBiomass / territory.ForestCover) : 0;
        else if (potential == "fish") ratio = basePotential > 0 ? Math.Max(0.08, territory.NaturalState.FishStock / basePotential) : 0;
        else if (potential is "clay" or "stone" or "iron_ore") ratio = territory.NaturalState.Deposits[potential];
        return 0.18 + basePotential * ratio * 0.82;
    }

    private static void Produce(WorldState world, ContentCatalog content, DailyTelemetry telemetry, IReadOnlyDictionary<string, double>? utilization, SettlementSimulation? development)
    {
        var recipes = content.Recipes.Recipes.ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId];
            var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            var effects = city.ActiveEffects.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            var workforceMultiplier = effects.Aggregate(1d, (value, pair) => value * pair.Value.Multiplier);
            var availableLabor = Math.Max(0, population * city.WorkerShare * workforceMultiplier - (development?.ReservedWorkerDays(cityId) ?? 0));
            foreach (var industry in city.Industries)
            {
                var recipe = recipes[industry.RecipeId];
                var planned = SimulationMath.Quantize(industry.Capacity * SeasonalityFactor(world, recipe) *
                    SiteFactor(world, industry, recipe) * TechnologyEfficiency(city, recipe) *
                    (utilization?.GetValueOrDefault(industry.Id, 1) ?? 1));
                var laborLimit = availableLabor / recipe.LaborPerBatch;
                var inputLimit = double.PositiveInfinity;
                foreach (var pair in recipe.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (pair.Value > 0) inputLimit = Math.Min(inputLimit, city.Stocks[pair.Key] / pair.Value);
                var batches = SimulationMath.Quantize(Math.Max(0, Math.Min(planned, Math.Min(laborLimit, inputLimit))));
                if (development is not null) batches = Math.Min(batches, Math.Min(laborLimit, inputLimit));
                batches = development?.LimitIndustry(world.Spatial.Territories[industry.ZoneId], recipe, batches) ?? batches;
                foreach (var pair in recipe.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var consumed = SimulationMath.Quantize(pair.Value * batches);
                    city.Stocks[pair.Key] = SimulationMath.Quantize(Math.Max(0, city.Stocks[pair.Key] - consumed));
                    telemetry.IndustrialConsumptionByResource[pair.Key] = SimulationMath.Quantize(telemetry.IndustrialConsumptionByResource.GetValueOrDefault(pair.Key) + consumed);
                }
                foreach (var pair in recipe.Outputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var produced = SimulationMath.Quantize(pair.Value * batches);
                    city.Stocks[pair.Key] = SimulationMath.Quantize(city.Stocks[pair.Key] + produced);
                    telemetry.ProductionByResource[pair.Key] = SimulationMath.Quantize(telemetry.ProductionByResource.GetValueOrDefault(pair.Key) + produced);
                }
                availableLabor = SimulationMath.Quantize(Math.Max(0, availableLabor - recipe.LaborPerBatch * batches));
                development?.RecordIndustryLabor(cityId, recipe.LaborPerBatch * batches);
                industry.TotalBatches = SimulationMath.Quantize(industry.TotalBatches + batches);
                if (development?.UseIndustryResource(world.Spatial.Territories[industry.ZoneId], recipe, batches) != true)
                    ApplyEnvironmentalUse(city, industry, recipe, world.Spatial.Territories[industry.ZoneId], batches);

                var constraints = new List<string>();
                var causes = new List<string?>();
                if (laborLimit + Epsilon < planned) { constraints.Add("labor"); causes.AddRange(effects.Select(pair => pair.Value.StartEventId)); }
                foreach (var pair in recipe.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    if (pair.Value > 0 && city.Stocks[pair.Key] + pair.Value * batches < pair.Value * planned - Epsilon)
                    { constraints.Add($"input:{pair.Key}"); causes.Add(city.ResourceSignals.GetValueOrDefault(pair.Key)); }
                UpdateConstraint(world, industry, recipe, city, planned, batches, constraints, causes);
            }
        }
    }

    private static void ApplyEnvironmentalUse(CityState city, IndustryState industry, RecipeDefinition recipe,
        Spatial.Territory territory, double batches)
    {
        if (recipe.SitePotential is null || batches <= 0) return;
        var natural = territory.NaturalState;
        var potential = recipe.SitePotential;
        if (potential == "arable") natural.SoilQuality = SimulationMath.Quantize(Math.Max(0.05, natural.SoilQuality - batches * 0.000055 * (1 - city.TechnologyState.GetValueOrDefault("crop_rotation")?.Adoption * 0.65 ?? 1)));
        else if (potential == "pasture") natural.SoilQuality = SimulationMath.Quantize(Math.Max(0.05, natural.SoilQuality - batches * 0.000025));
        else if (potential == "timber") natural.ForestBiomass = SimulationMath.Quantize(Math.Max(0, natural.ForestBiomass - batches * 0.00014));
        else if (potential == "fish") natural.FishStock = SimulationMath.Quantize(Math.Max(0, natural.FishStock - batches * 0.00016));
        else if (potential is "clay" or "stone" or "iron_ore")
        {
            var depletion = potential == "clay" ? 0.000025 : potential == "stone" ? 0.000018 : 0.00007;
            natural.Deposits[potential] = SimulationMath.Quantize(Math.Max(0, natural.Deposits[potential] - batches * depletion));
        }
        natural.ExtractedBatches[potential] = SimulationMath.Quantize(natural.ExtractedBatches.GetValueOrDefault(potential) + batches);
    }

    private static void UpdateConstraint(WorldState world, IndustryState industry, RecipeDefinition recipe, CityState city,
        double planned, double actual, List<string> constraints, List<string?> causes)
    {
        var key = string.Join('|', constraints);
        if (constraints.Count == 0)
        {
            if (industry.LastConstraintKey is not null)
                Journal.Record(world, "production_restored", industry.Id,
                    industry.ConstraintEventId is null ? [] : [industry.ConstraintEventId],
                    new JsonObject { ["cityId"] = city.Id, ["recipeId"] = recipe.Id });
            industry.LastConstraintKey = null; industry.ConstraintEventId = null; return;
        }
        if (key == industry.LastConstraintKey) return;
        var evt = Journal.Record(world, "production_constrained", industry.Id, causes,
            new JsonObject { ["cityId"] = city.Id, ["recipeId"] = recipe.Id, ["plannedBatches"] = planned,
                ["actualBatches"] = actual, ["constraints"] = JsonSerializer.SerializeToNode(constraints) });
        industry.LastConstraintKey = key; industry.ConstraintEventId = evt.Id;
        foreach (var resourceId in recipe.Outputs.Keys.Order(StringComparer.Ordinal)) city.ResourceSignals[resourceId] = evt.Id;
    }

    private static void ConsumeFood(WorldState world, DailyTelemetry telemetry, SettlementSimulation? development)
    {
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId];
            var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            var needed = SimulationMath.Quantize(population * city.FoodPerPersonPerDay);
            var localNeed = Math.Max(0, needed - (development?.RemoteNeed(cityId, "food") ?? 0));
            var localConsumed = SimulationMath.Quantize(Math.Min(localNeed, city.Stocks["food"]));
            development?.RecordFoodConsumption(city, localConsumed);
            var consumed = SimulationMath.Quantize(localConsumed + (development?.RemoteConsumption(cityId, "food") ?? 0));
            var missing = SimulationMath.Quantize(needed - consumed);
            city.Stocks["food"] = SimulationMath.Quantize(Math.Max(0, city.Stocks["food"] - localConsumed));
            telemetry.HouseholdFoodConsumed = SimulationMath.Quantize(telemetry.HouseholdFoodConsumed + consumed);
            telemetry.HouseholdFoodMissing = SimulationMath.Quantize(telemetry.HouseholdFoodMissing + missing);
            telemetry.HouseholdConsumptionByResource["food"] = SimulationMath.Quantize(telemetry.HouseholdConsumptionByResource.GetValueOrDefault("food") + consumed);
            telemetry.HouseholdMissingByResource["food"] = SimulationMath.Quantize(telemetry.HouseholdMissingByResource.GetValueOrDefault("food") + missing);
            if (missing > Epsilon)
            {
                city.Shortage.Days++; city.Shortage.EpisodeDays++; city.Shortage.MissingStreak++; city.Shortage.SatisfiedStreak = 0;
                city.Shortage.TotalFoodMissing = SimulationMath.Quantize(city.Shortage.TotalFoodMissing + missing);
                if (!city.Shortage.Active && city.Shortage.MissingStreak >= 2)
                {
                    var evt = Journal.Record(world, "food_shortage_started", cityId,
                        city.ResourceSignals.TryGetValue("food", out var cause) ? [cause] : [],
                        new JsonObject { ["cityId"] = cityId, ["needed"] = needed, ["available"] = consumed, ["missing"] = missing });
                    city.Shortage.Active = true; city.Shortage.EventId = evt.Id;
                }
            }
            else
            {
                city.Shortage.MissingStreak = 0;
                if (!city.Shortage.Active) city.Shortage.EpisodeDays = 0;
                if (city.Shortage.Active && ++city.Shortage.SatisfiedStreak >= 3)
                {
                    Journal.Record(world, "food_shortage_ended", cityId, [city.Shortage.EventId],
                        new JsonObject { ["cityId"] = cityId, ["durationDays"] = city.Shortage.EpisodeDays });
                    city.Shortage.Active = false; city.Shortage.EpisodeDays = 0; city.Shortage.SatisfiedStreak = 0; city.Shortage.EventId = null;
                }
            }
        }
    }

    private static void ConsumeOtherNeeds(WorldState world, ContentCatalog content, DailyTelemetry telemetry, SettlementSimulation? development)
    {
        var resources = content.Resources.Resources.Where(resource => resource.HouseholdNeed is not null && resource.Id != "food")
            .OrderBy(resource => resource.Id, StringComparer.Ordinal);
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        foreach (var resource in resources)
        {
            var city = world.Cities[cityId]; var state = city.Needs[resource.Id];
            var needed = SimulationMath.Quantize(NeedsAndDemand.DailyHouseholdNeed(world, city, resource));
            var localNeed = Math.Max(0, needed - (development?.RemoteNeed(cityId, resource.Id) ?? 0));
            var localConsumed = SimulationMath.Quantize(Math.Min(localNeed, city.Stocks[resource.Id]));
            var consumed = SimulationMath.Quantize(localConsumed + (development?.RemoteConsumption(cityId, resource.Id) ?? 0));
            var missing = SimulationMath.Quantize(needed - consumed);
            city.Stocks[resource.Id] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resource.Id] - localConsumed));
            telemetry.HouseholdConsumptionByResource[resource.Id] = SimulationMath.Quantize(telemetry.HouseholdConsumptionByResource.GetValueOrDefault(resource.Id) + consumed);
            telemetry.HouseholdMissingByResource[resource.Id] = SimulationMath.Quantize(telemetry.HouseholdMissingByResource.GetValueOrDefault(resource.Id) + missing);
            if (missing > Epsilon)
            {
                state.Days++; state.EpisodeDays++; state.MissingStreak++; state.SatisfiedStreak = 0;
                state.TotalMissing = SimulationMath.Quantize(state.TotalMissing + missing);
                if (!state.Active && state.MissingStreak >= 3)
                {
                    var evt = Journal.Record(world, "resource_shortage_started", $"{cityId}:{resource.Id}",
                        city.ResourceSignals.TryGetValue(resource.Id, out var cause) ? [cause] : [],
                        new JsonObject { ["cityId"] = cityId, ["resourceId"] = resource.Id, ["needed"] = needed, ["available"] = consumed, ["missing"] = missing });
                    state.Active = true; state.EventId = evt.Id;
                }
            }
            else
            {
                state.MissingStreak = 0; if (!state.Active) state.EpisodeDays = 0;
                if (state.Active && ++state.SatisfiedStreak >= 3)
                {
                    Journal.Record(world, "resource_shortage_ended", $"{cityId}:{resource.Id}", [state.EventId],
                        new JsonObject { ["cityId"] = cityId, ["resourceId"] = resource.Id, ["durationDays"] = state.EpisodeDays });
                    state.Active = false; state.EpisodeDays = 0; state.SatisfiedStreak = 0; state.EventId = null;
                }
            }
        }
    }
}
