using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private sealed class StorageSpace
    {
        public required string BuildingId { get; init; }
        public required SettlementBuildingRule Rule { get; init; }
        public double Initial { get; init; }
        public double Remaining { get; set; }
    }

    public void DecayStoredResources(DailyTelemetry telemetry)
    {
        if (Rules.Storage is not { } rules)
        {
            DecayWithoutStorage(telemetry);
            return;
        }
        var resources = content.Resources.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        foreach (var city in world.Cities.Values.OrderBy(city => city.Id, StringComparer.Ordinal))
        {
            var report = State.Cities[city.Id].Storage ??= new();
            report.TotalCapacity = report.UsedVolume = report.OutdoorVolume = report.LostToday = 0;
            report.CapacityByBuildingKind.Clear(); report.UsedByBuildingKind.Clear(); report.StoredByResource.Clear(); report.SpecializedByResource.Clear();
            report.OutdoorByResource.Clear(); report.LostByResource.Clear();
            var spaces = State.Buildings.Where(building => building.CityId == city.Id && building.Status == "active")
                .Select(building => (Building: building, Rule: BuildingRule(building.Kind)))
                .Where(item => item.Rule.StorageCapacity > 0)
                .Select(item => new StorageSpace
                {
                    BuildingId = item.Building.Id,
                    Rule = item.Rule,
                    Initial = item.Rule.StorageCapacity * Efficiency(item.Building),
                    Remaining = item.Rule.StorageCapacity * Efficiency(item.Building)
                }).Where(space => space.Remaining > 1e-9).OrderBy(space => space.BuildingId, StringComparer.Ordinal).ToList();
            foreach (var space in spaces)
            {
                report.TotalCapacity += space.Remaining;
                Add(report.CapacityByBuildingKind, space.Rule.Id, space.Remaining);
            }
            foreach (var resource in resources.Values.Where(resource => city.Stocks.GetValueOrDefault(resource.Id) > 0)
                         .OrderByDescending(resource => resource.DecayPerDay).ThenBy(resource => resource.Id, StringComparer.Ordinal))
            {
                var remaining = city.Stocks[resource.Id];
                var volume = rules.Volume(resource.Id, resource.Category);
                double lost = 0, stored = 0;
                foreach (var space in spaces.Where(space => space.Remaining > 1e-9)
                             .OrderBy(space => StorageDecayMultiplier(space.Rule, resource, rules))
                             .ThenBy(space => space.BuildingId, StringComparer.Ordinal))
                {
                    var amount = Math.Min(remaining, space.Remaining / volume);
                    if (amount <= 0) continue;
                    var multiplier = StorageDecayMultiplier(space.Rule, resource, rules);
                    lost += amount * Math.Min(.999999, resource.DecayPerDay * multiplier);
                    if (StoragePreferred(space.Rule, resource)) Add(report.SpecializedByResource, resource.Id, amount);
                    stored += amount; remaining -= amount; space.Remaining -= amount * volume;
                    if (remaining <= 1e-9) break;
                }
                if (remaining > 0)
                    lost += remaining * Math.Min(.999999, resource.DecayPerDay * rules.OutdoorDecayMultiplier);
                lost = SimulationMath.Quantize(Math.Min(city.Stocks[resource.Id], lost));
                city.Stocks[resource.Id] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resource.Id] - lost));
                report.StoredByResource[resource.Id] = SimulationMath.Quantize(stored);
                report.OutdoorByResource[resource.Id] = SimulationMath.Quantize(remaining);
                report.LostByResource[resource.Id] = lost;
                report.UsedVolume += stored * volume; report.OutdoorVolume += remaining * volume; report.LostToday += lost;
                if (lost > 0) Add(telemetry.DecayedByResource, resource.Id, lost);
            }
            foreach (var space in spaces) Add(report.UsedByBuildingKind, space.Rule.Id, Math.Max(0, space.Initial - space.Remaining));
            report.TotalCapacity = SimulationMath.Quantize(report.TotalCapacity);
            report.UsedVolume = SimulationMath.Quantize(report.UsedVolume);
            report.OutdoorVolume = SimulationMath.Quantize(report.OutdoorVolume);
            report.LostToday = SimulationMath.Quantize(report.LostToday);
            // Repeated sorting, covering and losing goods is the actual practice
            // from which dedicated storage can emerge. A tidy small household
            // learns slowly; visible overflow creates pressure quickly.
            Add(State.Cities[city.Id].PracticeHours, "storage", Math.Min(12, report.OutdoorVolume) + report.UsedVolume * .02);
            Add(State.Cities[city.Id].PracticeHours, "food_storage", Math.Min(12, FoodStorageNeedVolume(city)) +
                report.SpecializedByResource.Sum(pair => resources.TryGetValue(pair.Key, out var resource) ? pair.Value * rules.Volume(resource.Id, resource.Category) * .02 : 0));
        }
    }

    private static double StorageDecayMultiplier(SettlementBuildingRule building, ResourceDefinition resource, SettlementStorageRules rules)
    {
        if (building.Storage is not { } profile) return rules.GeneralBuildingDecayMultiplier;
        return StoragePreferred(building, resource) ? profile.DecayMultiplier : profile.FallbackDecayMultiplier;
    }
    private static bool StoragePreferred(SettlementBuildingRule building, ResourceDefinition resource) => building.Storage is { } profile &&
        (profile.Preferred.Contains(resource.Category, StringComparer.Ordinal) || profile.Preferred.Contains("resource:" + resource.Id, StringComparer.Ordinal));

    private void DecayWithoutStorage(DailyTelemetry telemetry)
    {
        var resources = content.Resources.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        foreach (var city in world.Cities.Values)
        foreach (var resourceId in city.Stocks.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            var decay = resources[resourceId].DecayPerDay;
            if (decay <= 0 || city.Stocks[resourceId] <= 0) continue;
            var lost = SimulationMath.Quantize(city.Stocks[resourceId] * decay);
            city.Stocks[resourceId] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resourceId] - lost));
            Add(telemetry.DecayedByResource, resourceId, lost);
        }
    }

    private double OutdoorStorageVolume(CityState city, bool foodOnly)
    {
        if (Rules.Storage is not { } rules || State.Cities[city.Id].Storage is not { } state) return 0;
        var resources = content.Resources.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        return state.OutdoorByResource.Where(pair => !foodOnly || resources.TryGetValue(pair.Key, out var resource) &&
                (resource.Category is "food_raw" or "food_intermediate" or "crop" or "seed" || pair.Key is "food" or "winter_food"))
            .Sum(pair => resources.TryGetValue(pair.Key, out var resource) ? pair.Value * rules.Volume(resource.Id, resource.Category) : 0);
    }

    private double FoodStorageNeedVolume(CityState city)
    {
        if (Rules.Storage is not { } rules || State.Cities[city.Id].Storage is not { } state) return 0;
        var resources = content.Resources.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        return resources.Values.Where(resource => resource.Category is "food_raw" or "food_intermediate" or "crop" or "seed" || resource.Id is "food" or "winter_food")
            .Sum(resource => (state.OutdoorByResource.GetValueOrDefault(resource.Id) + Math.Max(0,
                state.StoredByResource.GetValueOrDefault(resource.Id) - state.SpecializedByResource.GetValueOrDefault(resource.Id))) * rules.Volume(resource.Id, resource.Category));
    }
}
