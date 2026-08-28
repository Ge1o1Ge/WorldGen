using System.Text.Json.Nodes;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private readonly Func<CellAddress, ScoutTerrain>? surveyTerrain;
    private readonly Dictionary<(string, string), double> remoteNeeds = new();
    private readonly Dictionary<(string, string), double> remoteConsumption = new();
    public double RemoteNeed(string city, string resource) => remoteNeeds.GetValueOrDefault((city, resource));
    public double RemoteConsumption(string city, string resource) => remoteConsumption.GetValueOrDefault((city, resource));

    private (double Moisture, double Forest) TrailEnvironment(CellAddress cell)
    {
        if (terrain.TryGetValue(cell, out var t)) return (t.Moisture, t.NaturalState.ForestBiomass);
        var surveyed = surveyTerrain?.Invoke(cell);
        return (surveyed?.Moisture ?? .5, surveyed?.Forest ?? 0);
    }

    private ScoutTerrain? SurveyTerrain(CellAddress cell)
    {
        if (!terrain.TryGetValue(cell, out var t)) return surveyTerrain?.Invoke(cell);
        return new(t.Terrain == "water", t.Water.DistanceToRiver == 0, t.ElevationMeters, t.Moisture, t.NaturalState.ForestBiomass,
            new[] { "forage", "game", "fish" }.Sum(pool => Capacity(t, pool) * RecoveryRate(pool) * WeatherGrowth(cell)));
    }

    private void EvaluateSupply(CityState city)
    {
        if (Rules.Exploration is not { } rule || State.Cities[city.Id].Supply is not { } supply) return;
        var life = State.Cities[city.Id]; var need = Population(city) * city.FoodPerPersonPerDay;
        // Only the already materialized home range is used by this estimate.
        // Unseen terrain may be read by a scout at its location, not by city policy.
        var accessible = State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active" && b.Kind == "house")
            .SelectMany(b => Routes(b.Cell).Cost.Where(p => p.Value <= rule.SupplyRadiusCost).Select(p => p.Key)).Distinct()
            .Where(c => terrain[c].AssignedCityId == city.Id && terrain[c].Terrain != "water").ToArray();
        var renewable = 0d; var stock = 0d;
        foreach (var cell in accessible)
            foreach (var pool in new[] { "forage", "game", "fish" })
                if (pool == "fish" || layer.Construction.GetOccupiedCapacity(cell) == 0)
                { renewable += Capacity(terrain[cell], pool) * RecoveryRate(pool) * WeatherGrowth(cell); stock += Stock(terrain[cell], pool); }
        var garden = life.Tasks.Where(t => t.Activity is "garden" or "cultivate" or "food_preparation").Sum(t => t.Output);
        supply.AccessibleCells = accessible.Length;
        supply.FoodRenewalPerDay = renewable;
        supply.NaturalFoodStockDays = need > 0 ? stock / need : 0;
        supply.RenewalCoverage = need > 0 ? (renewable + garden) / need : 1;
        supply.FoodReserveDays = need > 0 ? Math.Max(0, city.Stocks["food"] + (Rules.Primitive is not null ? city.Stocks["winter_food"] : 0) - Math.Max(0, need - RemoteNeed(city.Id, "food"))) / need : 0;
        var essential = life.Tasks.Where(t => t.Activity is "gather" or "hunt" or "fish" or "garden" or "cultivate" or "food_preparation" or "seed_search" or "herd" or "water" or "fuel" or "remote_wood").Sum(t => t.Hours);
        supply.History.Add(new(world.Day, supply.FoodReserveDays, life.LaborAvailableHours > 0 ? essential / life.LaborAvailableHours : 1,
            life.WaterCoverage, supply.RenewalCoverage));
        while (supply.History.Count > rule.WindowDays) supply.History.RemoveAt(0);
        supply.LaborShare = supply.History.Average(d => d.LaborShare);
        supply.WaterCoverage = supply.History.Average(d => d.WaterCoverage);
        var reason = supply.History.Average(d => d.FoodReserveDays) < 1.5 ? "Малый запас пищи" :
            RemoteWoodPressure(city) ? "Ближний лес истощён: нужен разведанный промысловый участок" :
            supply.WaterCoverage < .95 ? "Недостаток доступной воды" :
            supply.LaborShare > rule.LaborPressureShare ? "Снабжение отнимает слишком много труда" :
            supply.History.Average(d => d.RenewalCoverage) < rule.MinimumRenewalCoverage && supply.NaturalFoodStockDays < 90
                ? "Местная пища расходуется быстрее восстановления" : null;
        var wasUnderPressure = supply.PressureStreak >= rule.PressureDays;
        supply.PressureStreak = reason is null ? 0 : Math.Min(rule.WindowDays + rule.PressureDays, supply.PressureStreak + 1);
        supply.Reason = reason ?? "Ближайшие окрестности обеспечивают снабжение";
        if (!wasUnderPressure && supply.PressureStreak == rule.PressureDays)
            supply.PressureEventId = Journal.Record(world, "supply_pressure", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["reason"] = reason }).Id;
        else if (wasUnderPressure && reason is null)
        {
            Journal.Record(world, "supply_pressure_relieved", city.Id, [supply.PressureEventId], new JsonObject { ["cityId"] = city.Id });
            supply.PressureEventId = null;
        }
        var expedition = State.Scouting?.Expeditions.FirstOrDefault(e => e.CityId == city.Id && e.Phase != "returned");
        supply.Action = expedition is not null ? (expedition.Phase == "returning" ? "Разведчики возвращаются с наблюдениями" : "Идёт обследование окрестностей") :
            !IsForager ? "Оценка природного снабжения; промышленный сценарий не отправляет разведчиков" :
            supply.PressureStreak < rule.PressureDays ? (reason is null ? "Расширение пока не требуется" : "Проверяем, устойчиво ли ухудшение") :
            world.Day - supply.LastDepartureDay < rule.CooldownDays ? "Повторная разведка после паузы; проверяем полученные сведения" :
            LaunchBlock(city) ?? "Готовится разведка на следующий день";
    }

    private string? LaunchBlock(CityState city)
    {
        var r = Rules.Exploration!; var available = State.Cities[city.Id].LaborAvailableHours;
        if (surveyTerrain is null) return "Нет доступа к неизученной местности";
        if (r.PartySize * Rules.WorkHoursPerDay > available * r.MaximumLaborShare) return "Недостаточно свободных работников для разведки";
        var food = r.PartySize * city.FoodPerPersonPerDay * r.ProvisionDays;
        var water = r.PartySize * WaterPerPerson() * r.ProvisionDays;
        if (city.Stocks["food"] < food + Population(city) * city.FoodPerPersonPerDay * r.HomeReserveDays || city.Stocks["water"] < water)
            return "Сначала нужен запас пищи и воды для похода";
        return null;
    }

    private double WaterPerPerson() => content.Resources.Resources.Single(r => r.Id == "water").HouseholdNeed!.PerPersonPerDay;

    private double RunScouting(CityState city, DailyTelemetry telemetry)
    {
        if (Rules.Exploration is not { } rule || State.Scouting is not { } scouting) return 0;
        var life = State.Cities[city.Id]; var supply = life.Supply!;
        supply.ScoutPeopleToday = 0; supply.ScoutLaborHours = 0;
        var expedition = scouting.Expeditions.FirstOrDefault(e => e.CityId == city.Id && e.Phase != "returned");
        // Bags use the same spoilage rules as city stores. Newly issued provisions
        // already decayed with the city this morning and must not decay twice.
        if (expedition is not null)
        {
            double Decay(string resource, double stock)
            {
                var lost = SimulationMath.Quantize(stock * content.Resources.Resources.Single(r => r.Id == resource).DecayPerDay);
                Add(telemetry.DecayedByResource, resource, lost);
                return Math.Max(0, stock - lost);
            }
            expedition.Food = Decay("food", expedition.Food);
            expedition.ProvisionComposition?.Reconcile(expedition.Food);
            expedition.Water = Decay("water", expedition.Water);
        }
        if (expedition is null && IsForager && supply.PressureStreak >= rule.PressureDays &&
            world.Day - supply.LastDepartureDay >= rule.CooldownDays && LaunchBlock(city) is null)
        {
            var home = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
            var origin = topology.ToUnitVector(home);
            var direction = topology.ToUnitVector(topology.GetNeighbor(home, (CardinalDirection)((scouting.NextId - 1) % 4)));
            expedition = new ScoutExpedition
            {
                Id = $"scout-{scouting.NextId++:000000}", CityId = city.Id, Home = home, Current = home,
                Direction = UnitVector3.Normalize(direction.X - origin.X, direction.Y - origin.Y, direction.Z - origin.Z),
                People = rule.PartySize, DepartureDay = world.Day, Reason = supply.Reason, Path = [home],
                Food = rule.PartySize * city.FoodPerPersonPerDay * rule.ProvisionDays,
                Water = rule.PartySize * WaterPerPerson() * rule.ProvisionDays
            };
            if (Rules.Wellbeing is not null)
            {
                var foodStock = life.Wellbeing!.FoodStock; foodStock.Reconcile(city.Stocks["food"]);
                expedition.ProvisionComposition = new() { Amounts = foodStock.Take(expedition.Food) };
            }
            city.Stocks["food"] -= expedition.Food; city.Stocks["water"] -= expedition.Water;
            scouting.Expeditions.RemoveAll(e => e.CityId == city.Id);
            scouting.Expeditions.Add(expedition); supply.LastDepartureDay = world.Day;
            expedition.CauseEventId = Journal.Record(world, "scouting_departed", city.Id, [supply.PressureEventId], new JsonObject
            { ["cityId"] = city.Id, ["expeditionId"] = expedition.Id, ["people"] = expedition.People, ["reason"] = expedition.Reason }).Id;
        }
        if (expedition is null) return 0;
        var hours = Math.Min(life.LaborAvailableHours, expedition.People * Rules.WorkHoursPerDay);
        supply.ScoutPeopleToday = expedition.People; supply.ScoutLaborHours = hours;
        ConsumeProvision(city.Id, "food", expedition.People * city.FoodPerPersonPerDay, expedition.Food, value => expedition.Food = value);
        expedition.ProvisionComposition?.Reconcile(expedition.Food);
        ConsumeProvision(city.Id, "water", expedition.People * WaterPerPerson(), expedition.Water, value => expedition.Water = value);
        expedition.LastStepDay = world.Day; expedition.LastLeg = [expedition.Current];
        if (world.Day - expedition.DepartureDay >= rule.OutboundDays) StartReturn(expedition);
        var perPersonHours = hours / expedition.People;
        for (var step = 0; step < rule.StepsPerDay && perPersonHours > 0; step++)
        {
            if (expedition.Phase == "returning" && expedition.ReturnIndex == 0) { CompleteSurvey(city, expedition); break; }
            CellAddress? next;
            if (expedition.Phase == "returning") next = expedition.Path[expedition.ReturnIndex - 1];
            else
            {
                var visited = expedition.Path.ToHashSet();
                // Only immediate neighbors are perceived; there is no global best-site query.
                next = topology.GetNeighbors(expedition.Current).Where(c => !visited.Contains(c) && SurveyTerrain(c) is { Water: false })
                    .OrderByDescending(c => topology.ToUnitVector(c).Dot(expedition.Direction))
                    .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Cast<CellAddress?>().FirstOrDefault();
                if (next is null) { StartReturn(expedition); continue; }
            }
            var currentTerrain = SurveyTerrain(expedition.Current); var nextTerrain = SurveyTerrain(next.Value);
            if (currentTerrain is null || nextTerrain is null || nextTerrain.Water) break;
            var travelHours = (1 + nextTerrain.Forest * .6 + Math.Abs(nextTerrain.Elevation - currentTerrain.Elevation) / 100) *
                (1 - trailStrength.GetValueOrDefault((expedition.Current, next.Value)) * trailRules.MaximumCostReduction) * WeatherWalking(next.Value) * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
            var cost = travelHours + (expedition.Phase == "outbound" ? rule.SurveyHoursPerCell : 0);
            if (cost > perPersonHours) break;
            perPersonHours -= cost;
            Passage([expedition.Current, next.Value], expedition.People);
            expedition.Current = next.Value; expedition.LastLeg.Add(next.Value);
            if (expedition.Phase == "returning") expedition.ReturnIndex--;
            else
            {
                expedition.Path.Add(next.Value);
                expedition.Observations.Add(new(next.Value, world.Day, nextTerrain.FreshWater, nextTerrain.FoodRenewalPerDay));
            }
        }
        if (expedition.Phase == "returning" && expedition.ReturnIndex == 0) CompleteSurvey(city, expedition);
        return hours;
    }

    private void ConsumeProvision(string city, string resource, double need, double available, Action<double> set)
    {
        var consumed = Math.Min(need, available); set(Math.Max(0, available - consumed));
        remoteNeeds[(city, resource)] = need; remoteConsumption[(city, resource)] = consumed;
    }

    private static void StartReturn(ScoutExpedition expedition)
    {
        if (expedition.Phase != "outbound") return;
        expedition.Phase = "returning"; expedition.ReturnIndex = expedition.Path.Count - 1;
    }

    private void CompleteSurvey(CityState city, ScoutExpedition expedition)
    {
        if (expedition.Phase == "returned") return;
        expedition.Phase = "returned"; expedition.ReturnDay = world.Day;
        if (Rules.Wellbeing is not null && expedition.ProvisionComposition is { } composition)
        {
            var stock = State.Cities[city.Id].Wellbeing!.FoodStock; stock.Reconcile(city.Stocks["food"]);
            composition.Reconcile(expedition.Food);
            foreach (var item in composition.Take(expedition.Food)) stock.Add(item.Key, item.Value);
        }
        city.Stocks["food"] += expedition.Food; city.Stocks["water"] += expedition.Water;
        expedition.Food = expedition.Water = 0;
        expedition.ProvisionComposition?.Reconcile(0);
        var unknown = expedition.Observations.Where(o => !terrain.ContainsKey(o.Cell)).ToArray();
        var candidates = unknown.Where(o => o.FreshWater && o.FoodRenewalPerDay > 0)
            .OrderByDescending(o => o.FoodRenewalPerDay).ThenBy(o => SphericalSimulation.ZoneId(o.Cell), StringComparer.Ordinal).Take(5).ToArray();
        var outcome = candidates.Length > 0 ? "Найдены участки у пресной воды; нужна оценка окрестностей перед переселением" :
            unknown.Length > 0 ? "Направление обследовано, пригодный участок у воды не найден" : "Не удалось выйти за пределы знакомой окрестности";
        var supply = State.Cities[city.Id].Supply!;
        supply.Reports.Add(new(expedition.Id, expedition.DepartureDay, world.Day, unknown.Length, candidates, outcome));
        while (supply.Reports.Count > Rules.Exploration!.MaximumReports) supply.Reports.RemoveAt(0);
        Journal.Record(world, "scouting_returned", city.Id, [expedition.CauseEventId], new JsonObject
        { ["cityId"] = city.Id, ["expeditionId"] = expedition.Id, ["surveyedCells"] = unknown.Length, ["candidates"] = candidates.Length, ["outcome"] = outcome });
    }
}
