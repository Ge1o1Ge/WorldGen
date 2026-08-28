using System.Text.Json.Nodes;
using WorldGen.Core.Spatial;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private readonly Dictionary<CellAddress, double> gardenTaken = new();
    public double EncounterRate(Territory t, string pool) => Rules.Subsistence is not null && pool == "timber"
        ? Math.Pow(Math.Clamp(Stock(t, pool) / Math.Max(1e-9, Capacity(t, pool)), 0, 1), Rules.Subsistence.EncounterExponent) :
        Rules.Subsistence is { } rules && pool is "forage" or "game" or "fish"
        ? rules.PrimitiveEfficiency[pool] * Math.Pow(Math.Clamp(Stock(t, pool) / Math.Max(1e-9, Capacity(t, pool)), 0, 1), rules.EncounterExponent) /
            (1 + (State.HarvestPressure?.GetValueOrDefault(t.Id)?.GetValueOrDefault(pool) ?? 0) + (pool == "game" ? WildlifeAlert(t) : 0)) : 1;

    private void RecordHarvestPressure(Territory t, string pool, double amount)
    {
        if (Rules.Subsistence is not { } rules || State.HarvestPressure is not { } pressure || amount <= 0 || !rules.EasyCatchTonnes.ContainsKey(pool)) return;
        void AddPressure(string id, double value)
        {
            if (!pressure.TryGetValue(id, out var site)) pressure[id] = site = new(StringComparer.Ordinal);
            Add(site, pool, value);
        }
        var increase = amount / rules.EasyCatchTonnes[pool];
        AddPressure(t.Id, increase);
        // Local disturbance remains after a mobile group has fled the hunters.
        if (pool == "game")
            foreach (var adjacent in topology.GetNeighbors(addresses[t.Id]))
                if (terrain.TryGetValue(adjacent, out var neighbor) && neighbor.Terrain != "water") AddPressure(neighbor.Id, increase * .35);
    }
    private void RecoverHarvestPressure()
    {
        if (Rules.Subsistence is not { } rules || State.HarvestPressure is not { } pressure) return;
        foreach (var site in pressure.Keys.ToArray())
        {
            foreach (var pool in pressure[site].Keys.ToArray())
            {
                pressure[site][pool] *= Math.Pow(.5, 1 / rules.PressureHalfLifeDays[pool]);
                if (pressure[site][pool] < 1e-6) pressure[site].Remove(pool);
            }
            if (pressure[site].Count == 0) pressure.Remove(site);
        }
    }

    private IEnumerable<HouseholdActivityRule> HouseholdActivities(CityState city)
    {
        foreach (var activity in Rules.Activities)
            if (Rules.Subsistence is null || activity.Id != "garden") yield return activity;
        if (Rules.Subsistence is { } rules && BiologyRules is null)
            yield return new("cultivate", "Уход за освоенным огородом", "food", rules.GardenOutputPerHour, .65, null, "gardening", new Dictionary<string, double>());
        if (Rules.Lifecycle is not null)
        {
            yield return new("clay", "Сбор глины", "clay", .002, .15, "clay", null, new Dictionary<string, double>());
            yield return new("stone", "Заготовка камня", "stone", .0005, .15, "stone", Rules.Primitive is null ? "masonry" : "building", new Dictionary<string, double>());
        }
    }
    private bool ReadyGarden(DwellingState b) => b.Kind == "garden" && b.Status == "active" && b.ReadyDay <= world.Day;
    private double GardenSoil(CellAddress cell) => gardenSoilToday.GetValueOrDefault(cell, terrain[cell].NaturalState.SoilQuality);
    private double GardenYield(CellAddress cell) => BiologyRules is not null ? CropExpectedDailyYield(cell) : (Rules.Subsistence?.GardenDailyYield ?? 0) * GardenSoil(cell) * WeatherGrowth(cell);
    private double GardenRemaining(CellAddress cell) => Math.Max(0, GardenYield(cell) - gardenTaken.GetValueOrDefault(cell));
    private CellAddress? ActivitySite(CityState city, CellAddress origin, HouseholdActivityRule activity)
    {
        if (activity.Pool is { } pool) return BestResourceSite(origin, pool);
        if (activity.Id != "cultivate") return origin;
        var route = Routes(origin);
        return State.Buildings.Where(b => b.CityId == city.Id && ReadyGarden(b) && GardenRemaining(b.Cell) > 1e-9 && route.Cost.ContainsKey(b.Cell))
            .OrderByDescending(b => terrain[b.Cell].NaturalState.SoilQuality * Math.Max(0, 1 - route.Cost[b.Cell] * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay))
            .ThenBy(b => b.Id, StringComparer.Ordinal).Select(b => (CellAddress?)b.Cell).FirstOrDefault();
    }
    private double ExpectedRate(CityState city, CellAddress origin, HouseholdActivityRule activity)
    {
        if (Rules.Subsistence is null) return activity.OutputPerHour;
        var site = ActivitySite(city, origin, activity);
        if (site is null) return 0;
        var rate = activity.OutputPerHour * Math.Max(0, 1 - Routes(origin).Cost[site.Value] * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay);
        if (activity.Pool is { } pool) rate *= EncounterRate(terrain[site.Value], pool);
        if (activity.Id == "cultivate") rate *= GardenSoil(site.Value);
        return rate * PrimitiveActivityFactor(city, activity, site.Value);
    }
    private SettlementBuildingRule BuildingRule(string kind) => kind == "garden" && Rules.Subsistence is { } rules
        ? new("garden", rules.GardenLaborHours, new Dictionary<string, double> { ["timber"] = rules.GardenTimber })
        : Rules.Buildings.Single(b => b.Id == kind);

    private void RecordFoodTask(SettlementLifeState life, HouseholdActivityRule activity, double hours, double amount, double travel, double distance)
    {
        if (activity.Output != "food" || life.Food is not { } food) return;
        food.LaborHours += hours;
        food.TravelHours += hours / Rules.WorkHoursPerDay * travel;
        food.MeanOneWayMeters += hours * distance * world.Spatial.Grid.ZoneSizeMeters;
        if (activity.Pool is not null) { food.WildHours += hours; food.WildOutput += amount; }
        else food.GardenOutput += amount;
    }
    private void UpdateFoodSummary(CityState city)
    {
        if (State.Cities[city.Id].Food is not { } food) return;
        food.MeanOneWayMeters /= Math.Max(1e-9, food.LaborHours);
        food.ReadyGardens = State.Buildings.Count(b => b.CityId == city.Id && ReadyGarden(b));
        food.PreparingGardens = State.Buildings.Count(b => b.CityId == city.Id && b.Kind == "garden" && Standing(b) && !ReadyGarden(b));
    }
    private bool Moving(DwellingState b) => b.Kind == "house" && b.Status == "active" && b.Replaces is not null && b.MoveFinished == false;
    private void MoveHouseholds(CityState city)
    {
        var life = State.Cities[city.Id];
        foreach (var target in State.Buildings.Where(b => b.CityId == city.Id && Moving(b)))
        {
            var source = State.Buildings.Single(b => b.Id == target.Replaces);
            var path = Routes(source.Cell);
            if (!path.Cost.TryGetValue(target.Cell, out var distance)) continue;
            var cost = (Rules.Subsistence?.MovingHoursPerPerson ?? 2) + distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
            var count = Math.Min(Rules.Subsistence?.MoversPerDay ?? 3, Math.Min(source.Residents, Rules.ResidentsPerHouse - target.Residents));
            count = Math.Min(count, (int)Math.Floor(Math.Max(0, life.LaborAvailableHours - life.LaborUsedHours) / cost));
            if (count > 0)
            {
                TransferWellbeing(city.Id, HouseholdIdentity(source.Id), HouseholdIdentity(target.Id), count, target.Residents);
                TransferHouseholdVoice(life.Council, source, target, count);
                source.Residents -= count; target.Residents += count;
                life.LaborUsedHours += count * cost;
                if (life.Food is { } food) food.MovedToday += count;
                life.Tasks.Add(new(source.Id, "move", target.Cell, count * cost, count));
                Passage(path.Path(target.Cell), count * 2);
            }
            if (source.Residents > 0 && target.Residents < Rules.ResidentsPerHouse) continue;
            target.MoveFinished = true; life.LastRelocationDay = world.Day;
            // Empty housing is abandoned only after the normal disuse interval.
            Journal.Record(world, "household_relocated", city.Id, [target.CauseEventId], new JsonObject
            { ["cityId"] = city.Id, ["from"] = source.Id, ["to"] = target.Id, ["remainingResidents"] = source.Residents });
        }
    }
    private void TransferHouseholdVoice(CollectiveDecisionState? council, DwellingState source, DwellingState target, int count)
    {
        if (council is null) return;
        var fromId = HouseholdIdentity(source.Id); var toId = HouseholdIdentity(target.Id);
        if (fromId == toId || !council.Profiles.TryGetValue(fromId, out var from)) return;
        if (!council.Profiles.TryGetValue(toId, out var to)) council.Profiles[toId] = to = new();
        var share = count / (double)Math.Max(1, source.Residents);
        foreach (var domain in from.PracticeHours.Keys.ToArray())
        { var hours = from.PracticeHours[domain] * share; from.PracticeHours[domain] -= hours; Add(to.PracticeHours, domain, hours); }
        foreach (var domain in from.Reputation.Keys.Concat(to.Reputation.Keys).Distinct().ToArray())
            to.Reputation[domain] = (to.Reputation.GetValueOrDefault(domain, 1) * target.Residents + from.Reputation.GetValueOrDefault(domain, 1) * count) / (target.Residents + count);
        from.Members = source.Residents - count; to.Members = target.Residents + count;
        // Past support follows the people, without multiplying old votes. The same
        // split applies to location disputes and to future outcome responsibility.
        foreach (var backers in council.Proposals.SelectMany(p => p.Sites.Select(s => s.Backers).Prepend(p.Backers)))
        {
            foreach (var backing in backers.Where(b => b.SourceId == fromId || b.DeciderId == fromId).ToArray())
            {
                var points = backing.Points * share; backing.Points -= points;
                var sourceId = backing.SourceId == fromId ? toId : backing.SourceId;
                var deciderId = backing.DeciderId == fromId ? toId : backing.DeciderId;
                var existing = backers.FirstOrDefault(b => b.SourceId == sourceId && b.DeciderId == deciderId);
                if (existing is not null) existing.Points += points;
                else backers.Add(new() { SourceId = sourceId, DeciderId = deciderId, Points = points });
            }
            backers.RemoveAll(b => b.Points <= 1e-12);
        }
    }
}
