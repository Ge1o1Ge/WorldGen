using System.Text.Json.Nodes;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private readonly Dictionary<(string City, string Kind), BuildingMaterialRule> materialChoices = new();
    private readonly Dictionary<CellAddress, double> gardenSoilToday = new();
    private static bool Standing(DwellingState b) => b.Status is "active" or "building";
    private double Efficiency(DwellingState b) => Rules.Lifecycle is null ? 1 : b.Lifecycle?.Efficiency ?? 1;
    private BuildingMaterialRule Material(DwellingState b) => Rules.Lifecycle!.Materials.Single(m => m.Id == b.Lifecycle!.Material);

    private void InitializeLifecycle(DwellingState b, bool baseline, BuildingMaterialRule? material = null)
    {
        if (Rules.Lifecycle is not { } rules) return;
        if (b.Kind == "garden")
        {
            b.Field ??= new() { FallowSinceDay = b.Status == "abandoned" ? world.Day : null };
            return;
        }
        b.Lifecycle ??= new()
        {
            Material = (material ?? rules.Materials.Single(m => m.Id == (Rules.Primitive is null ? "wood" : "clay_straw"))).Id,
            AccountedFromDay = world.Day,
            LastAgedDay = world.Day,
            BaselineAssessment = baseline
        };
        _ = Material(b); // Reject unsupported saved material ids, never silently substitute them.
        if (b.Kind == "well") b.Well ??= new()
        {
            LastRechargeDay = world.Day,
            Capacity = b.Status == "active" ? rules.WellCapacity * Efficiency(b) : 0,
            RechargeRate = b.Status == "active" ? rules.WellRechargePerDay * (.5 + terrain[b.Cell].Moisture * .5) * Efficiency(b) : 0
        };
    }

    private void AgeBuildingsAndRechargeWells()
    {
        if (Rules.Lifecycle is not { } rules) return;
        foreach (var b in State.Buildings)
        {
            if (b.Lifecycle is { } age && b.Status is "active" or "abandoned" or "demolishing")
                age.Age(Material(b), world.Day, b.Status == "active" && b.UnusedDays > 0 ? rules.UnusedWearMultiplier : 1);
            if (b.Well is not { } well || well.LastRechargeDay >= world.Day) continue;
            var elapsed = world.Day - well.LastRechargeDay;
            well.LastRechargeDay = world.Day;
            well.WithdrawnToday = well.RechargedToday = well.OverflowToday = 0;
            well.Capacity = b.Status == "active" ? rules.WellCapacity * Efficiency(b) : 0;
            well.RechargeRate = b.Status == "active" ? rules.WellRechargePerDay * (.5 + terrain[b.Cell].Moisture * .5) * Efficiency(b) * WeatherRecharge(b.Cell) : 0;
            well.OverflowToday = Math.Max(0, well.Stock - well.Capacity);
            well.Stock -= well.OverflowToday;
            well.RechargedToday = Math.Min(well.Capacity - well.Stock, well.RechargeRate * elapsed);
            well.Stock = Math.Clamp(well.Stock + well.RechargedToday, 0, well.Capacity);
        }
    }

    private double CollectStoredWater(CityState city, DwellingState home, double budget, double requested, DailyTelemetry telemetry)
    {
        var life = State.Cities[city.Id]; var route = Routes(home.Cell);
        var wells = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "well" && b.Status == "active" && b.Well is not null)
            .GroupBy(b => b.Cell).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray());
        var sources = route.Cost.Keys.Where(c => terrain[c].Terrain != "water" && (terrain[c].Water.DistanceToRiver == 0 || wells.ContainsKey(c)))
            .OrderBy(c => route.Cost[c]).ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal);
        var remaining = Math.Min(requested, Math.Max(0, Target(city, "water") - city.Stocks["water"]));
        var spent = 0d;
        foreach (var source in sources)
        {
            if (remaining <= 1e-9 || spent >= budget * .65) break;
            var river = terrain[source].Water.DistanceToRiver == 0;
            var stock = river ? Math.Max(0, 2 - waterTaken.GetValueOrDefault(source)) : wells[source].Sum(b => b.Well!.Stock);
            var distance = route.Cost[source] * world.Spatial.Grid.ZoneSizeMeters;
            var tripHours = distance * 2 / Rules.WalkingMetersPerHour + .08;
            var amount = Math.Min(remaining, Math.Min(stock, (budget * .65 - spent) / tripHours * WaterCarry(city)));
            if (amount <= 1e-9) continue;
            if (river) waterTaken[source] = waterTaken.GetValueOrDefault(source) + amount;
            else
            {
                var draw = amount;
                foreach (var b in wells[source])
                {
                    var taken = Math.Min(draw, b.Well!.Stock); b.Well.Stock = Math.Max(0, b.Well.Stock - taken); b.Well.WithdrawnToday += taken; draw -= taken;
                }
            }
            var trips = amount / WaterCarry(city); var hours = trips * tripHours;
            spent += hours; remaining -= amount;
            city.Stocks["water"] += amount; life.WaterCollected += amount; life.WaterTravelHours += trips * distance * 2 / Rules.WalkingMetersPerHour;
            Add(life.Production, "water", amount); Add(telemetry.ProductionByResource, "water", amount); Add(life.PracticeHours, "water", hours);
            life.Tasks.Add(new(home.Id, "water", source, hours, amount)); Passage(route.Path(source), trips * 2);
        }
        return spent;
    }

    private bool NeedsReplacement(DwellingState b) => Rules.Lifecycle is { } r && b.Lifecycle is { } age && b.Status == "active" && !age.Retiring &&
        (age.AgeDays >= Material(b).ServiceLifeDays * r.ReplacementAgeShare || Efficiency(b) < r.ReplacementEfficiency);

    private double MaterialFactor(string kind) => BuildingRule(kind).Materials.GetValueOrDefault("timber") /
        Math.Max(1e-9, BuildingRule("house").Materials.GetValueOrDefault("timber"));

    private BuildingMaterialRule ChooseMaterial(CityState city, string kind)
    {
        if (materialChoices.TryGetValue((city.Id, kind), out var cached)) return cached;
        var life = State.Cities[city.Id];
        var origin = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
        double GatherHours(string resource, double amount)
        {
            var activity = HouseholdActivities(city).FirstOrDefault(a => a.Output == resource && a.Pool is not null);
            if (activity is null) return double.PositiveInfinity;
            var site = BestResourceSite(origin, activity.Pool!);
            if (site is null) return double.PositiveInfinity;
            var rate = activity.OutputPerHour * EncounterRate(terrain[site.Value], activity.Pool!) *
                Math.Max(0, 1 - Routes(origin).Cost[site.Value] * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay);
            return amount / Math.Max(1e-9, rate);
        }
        double AnnualCost(BuildingMaterialRule m)
        {
            var construction = BuildingRule(kind).LaborHours * m.LaborMultiplier * PrimitiveMaterialLabor(city, m.Id);
            var gathering = m.Materials.Sum(p => GatherHours(p.Key, p.Value * MaterialFactor(kind)));
            return (construction + gathering + m.DemolitionHours) / (m.ServiceLifeDays / 365d) +
                m.AnnualWear * (1 - m.PermanentShare) * (m.RepairLaborPerWear + gathering * m.RepairMaterialMultiplier);
        }
        var chosen = Rules.Lifecycle!.Materials.Where(m => m.Discovery is null || life.Discoveries.Contains(m.Discovery))
            .OrderBy(AnnualCost).ThenBy(m => m.Id, StringComparer.Ordinal).First();
        materialChoices[(city.Id, kind)] = chosen;
        return chosen;
    }

    private SettlementBuildingRule ProjectRule(CityState city, string kind)
    {
        var rule = BuildingRule(kind);
        if (Rules.Lifecycle is null || kind == "garden") return rule;
        var m = ChooseMaterial(city, kind);
        var structural = new HashSet<string>(["timber", "stone", "clay", "fiber"], StringComparer.Ordinal);
        var materials = m.Materials.ToDictionary(p => p.Key, p => p.Value * MaterialFactor(kind), StringComparer.Ordinal);
        // A lifecycle material replaces the structural shell, but cannot erase
        // machinery, vessels or other functional components from a specialised building.
        foreach (var component in rule.Materials.Where(pair => !structural.Contains(pair.Key)))
            materials[component.Key] = materials.GetValueOrDefault(component.Key) + component.Value;
        return rule with { LaborHours = rule.LaborHours * m.LaborMultiplier * PrimitiveMaterialLabor(city, m.Id), Materials = materials };
    }

    private double MaintenanceTarget(CityState city, string resource)
    {
        if (Rules.Lifecycle is null) return 0;
        var repair = State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active" && b.Lifecycle is not null && !b.Lifecycle.Retiring)
            .Sum(b => Math.Max(.03, b.Lifecycle!.RepairableWear) * Material(b).Materials.GetValueOrDefault(resource) * Material(b).RepairMaterialMultiplier * MaterialFactor(b.Kind));
        // Accumulate only materials for known, currently discussed projects, not every possible recipe.
        // Council proposals are no longer synonymous with construction: scouting is
        // an executable expedition and has no building recipe or material reserve.
        var buildingKinds = Rules.Buildings.Select(rule => rule.Id).Append("garden").ToHashSet(StringComparer.Ordinal);
        var kinds = State.Cities[city.Id].Council?.Proposals
            .Where(p => p.Available && CollectiveDecisions.Pending(p) && buildingKinds.Contains(p.Kind))
            .Select(p => p.Kind).Distinct().ToArray() ?? [];
        return repair + kinds.Select(kind => ProjectRule(city, kind).Materials.GetValueOrDefault(resource)).DefaultIfEmpty().Max();
    }

    private void MaintainBuildings(CityState city, DailyTelemetry telemetry)
    {
        if (Rules.Lifecycle is not { } rules) return;
        var life = State.Cities[city.Id]; var report = life.Maintenance!;
        var origin = State.Buildings.FirstOrDefault(b => b.CityId == city.Id && b.Residents > 0)?.Cell ?? addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
        var routesFromHome = Routes(origin);
        double Fraction(DwellingState b) => routesFromHome.Cost.TryGetValue(b.Cell, out var distance) ?
            Math.Max(0, 1 - distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay) : 0;
        double Available() => Math.Max(0, Math.Min(life.LaborAvailableHours - life.LaborUsedHours,
            life.LaborAvailableHours * rules.MaintenanceLaborShare - report.RepairHours - report.DemolitionHours));
        foreach (var b in State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active" && b.Lifecycle is not null && b.UnusedDays == 0)
            .OrderBy(b => Efficiency(b)).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            var age = b.Lifecycle!; var material = Material(b); var productive = Fraction(b);
            if (age.RepairableWear < rules.RepairTrigger || age.Retiring || productive <= 0 || age.PermanentWear >= 1 - rules.UnsafeEfficiency) continue;
            var fixedWear = Math.Min(age.RepairableWear, Available() * productive / material.RepairLaborPerWear);
            var costs = material.Materials.ToDictionary(p => p.Key, p => p.Value * material.RepairMaterialMultiplier * MaterialFactor(b.Kind));
            foreach (var cost in costs) fixedWear = Math.Min(fixedWear, city.Stocks[cost.Key] / cost.Value);
            if (fixedWear <= 1e-9) continue;
            foreach (var cost in costs)
            {
                var amount = cost.Value * fixedWear; city.Stocks[cost.Key] = Math.Max(0, city.Stocks[cost.Key] - amount);
                Add(report.MaterialsUsed, cost.Key, amount); Add(telemetry.InfrastructureConsumptionByResource, cost.Key, amount);
            }
            var hours = fixedWear * material.RepairLaborPerWear / productive;
            age.RepairableWear -= fixedWear; life.LaborUsedHours += hours; report.RepairHours += hours;
            Add(life.PracticeHours, "construction", hours * productive);
            life.Tasks.Add(new(b.Id, "repair", b.Cell, hours, fixedWear)); Passage(routesFromHome.Path(b.Cell), hours / Rules.WorkHoursPerDay * 2);
        }
        // Safety is not a license to erase occupants: RehousePopulation keeps displaced people at the camp.
        foreach (var b in State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active" && b.Lifecycle is not null && Efficiency(b) <= rules.UnsafeEfficiency).ToArray())
            Abandon(b, "Постройка небезопасна из-за износа");
        ReconcileRetiredBuildings(city);
        foreach (var b in State.Buildings.Where(b => b.CityId == city.Id && b.Lifecycle is not null && b.Status is "abandoned" or "demolishing")
            .OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            if (b.Residents != 0) continue;
            var productive = Fraction(b); if (productive <= 0) continue;
            var age = b.Lifecycle!; var material = Material(b);
            var hours = Math.Min(Available(), Math.Max(0, material.DemolitionHours - age.DemolitionDone) / productive);
            if (hours <= 1e-9) continue;
            b.Status = "demolishing"; age.DemolitionDone += hours * productive;
            report.DemolitionHours += hours; life.LaborUsedHours += hours;
            life.Tasks.Add(new(b.Id, "demolition", b.Cell, hours, 0)); Passage(routesFromHome.Path(b.Cell), hours / Rules.WorkHoursPerDay * 2);
            if (age.DemolitionDone + 1e-8 < material.DemolitionHours) continue;
            b.Status = "demolished"; layer.Construction.Remove(b.Id);
            // Only paid construction materials can be salvaged, never unknown legacy investment or repairs.
            foreach (var paid in age.InvestedMaterials)
            {
                var amount = paid.Value * material.SalvageShare * (1 - age.PermanentWear);
                city.Stocks[paid.Key] += amount; Add(report.Salvaged, paid.Key, amount); Add(telemetry.ProductionByResource, paid.Key, amount);
            }
            Journal.Record(world, "settlement_building_demolished", b.Id, [b.CauseEventId], new JsonObject { ["cityId"] = city.Id, ["reason"] = "Оплаченный разбор пустующей постройки; участок освобождён" });
        }
    }

    private void ReconcileRetiredBuildings(CityState city)
    {
        foreach (var old in State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active" && b.Lifecycle?.Retiring == true && b.Residents == 0).ToArray())
            Abandon(old, "Замена готова; старое строение освобождено и может быть разобрано");
    }

    private void CompleteLifecycle(DwellingState project)
    {
        if (project.Lifecycle is { } age) { age.AgeDays = 0; age.AccountedFromDay = age.LastAgedDay = world.Day; }
        if (project.Well is { } well && Rules.Lifecycle is { } rules)
        {
            well.Capacity = rules.WellCapacity;
            well.RechargeRate = rules.WellRechargePerDay * (.5 + terrain[project.Cell].Moisture * .5);
        }
        if (Rules.Lifecycle is null || project.Replaces is not { } oldId) return;
        var old = State.Buildings.Single(b => b.Id == oldId);
        if (old.Lifecycle is { } previous) previous.Retiring = true;
    }

    private void RecordSoilHarvest(CellAddress cell, double amount)
    {
        if (Rules.Lifecycle is not { } rules) return;
        var b = State.Buildings.First(b => b.Cell == cell && ReadyGarden(b));
        b.Field!.Harvested += amount;
        terrain[cell].NaturalState.SoilQuality = Math.Max(0, terrain[cell].NaturalState.SoilQuality - amount * rules.SoilLossPerTonne);
    }

    private bool CanCultivate(CellAddress cell) => Rules.Lifecycle is not { } r ||
        terrain[cell].NaturalState.SoilQuality >= Math.Max(.35, r.MinimumFieldOutputPerHour / Math.Max(1e-9, Rules.Subsistence?.GardenOutputPerHour ?? 0) * 1.5) &&
        !State.Buildings.Any(b => b.Cell == cell && b.Field?.FallowSinceDay is { } day && world.Day - day < r.MeadowRecoveryDays);

    private void EvaluateFields(CityState city)
    {
        if (Rules.Lifecycle is not { } rules) return;
        var homes = State.Buildings.Where(b => b.CityId == city.Id && b.Residents > 0).ToArray();
        foreach (var b in State.Buildings.Where(b => b.CityId == city.Id && ReadyGarden(b)).ToArray())
        {
            if(BiologyRules is not null)
            {
                var plot=State.Cities[city.Id].Biology!.Plots.GetValueOrDefault(b.Id);
                if(plot is { FailedSeasons: >=3 } || terrain[b.Cell].NaturalState.SoilQuality<.15)
                    Abandon(b,"Несколько неудачных сезонов или истощение почвы; участок оставлен под залежь");
                continue;
            }
            if (FieldDormant(b.Cell)) { b.Field!.PoorYieldDays = 0; continue; }
            var meanFraction = homes.Sum(h => h.Residents * (Routes(h.Cell).Cost.TryGetValue(b.Cell, out var distance) ?
                Math.Max(0, 1 - distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay) : 0)) / Math.Max(1, homes.Sum(h => h.Residents));
            var field = b.Field!;
            field.ExpectedOutputPerHour = Rules.Subsistence!.GardenOutputPerHour * terrain[b.Cell].NaturalState.SoilQuality * meanFraction;
            field.PoorYieldDays = field.ExpectedOutputPerHour < rules.MinimumFieldOutputPerHour ? field.PoorYieldDays + 1 : 0;
            if (field.PoorYieldDays >= rules.PoorHarvestDays) Abandon(b, "Урожай с учётом дороги больше не оправдывает труд; участок оставлен под залежь");
        }
    }

    private void UpdateMaintenanceSummary(CityState city)
    {
        if (State.Cities[city.Id].Maintenance is not { } m) return;
        var buildings = State.Buildings.Where(b => b.CityId == city.Id).ToArray();
        var active = buildings.Where(b => b.Status == "active" && b.Lifecycle is not null).ToArray();
        m.MeanEfficiency = active.Length == 0 ? 0 : active.Average(Efficiency);
        m.RepairableWear = active.Sum(b => b.Lifecycle!.RepairableWear);
        m.PermanentWear = active.Sum(b => b.Lifecycle!.PermanentWear);
        m.ReplacementNeeded = active.Count(NeedsReplacement);
        m.Demolished = buildings.Count(b => b.Status == "demolished");
        m.FallowFields = buildings.Where(b => b.Field?.FallowSinceDay is not null).Select(b => b.Cell).Distinct()
            .Count(c => !buildings.Any(b => b.Cell == c && Standing(b)));
    }
}
