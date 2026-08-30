using System.Text.Json.Nodes;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private sealed record ScoutPlan(int ProvisionDays, int OutboundDays, double Capacity, double Food, double Water,
        bool RaftReady, double RaftTimber, double SpeedMultiplier);
    private readonly Func<CellAddress, ScoutTerrain>? surveyTerrain;
    private readonly Dictionary<(string, string), double> remoteNeeds = new();
    private readonly Dictionary<(string, string), double> remoteConsumption = new();
    public double RemoteNeed(string city, string resource) => remoteNeeds.GetValueOrDefault((city, resource));
    public double RemoteConsumption(string city, string resource) => remoteConsumption.GetValueOrDefault((city, resource));

    private static bool ActiveScout(ScoutExpedition expedition) => expedition.Phase is "outbound" or "returning";

    private (double Moisture, double Forest) TrailEnvironment(CellAddress cell)
    {
        if (terrain.TryGetValue(cell, out var t)) return (t.Moisture, t.NaturalState.ForestBiomass);
        var surveyed = surveyTerrain?.Invoke(cell);
        return (surveyed?.Moisture ?? .5, surveyed?.Forest ?? 0);
    }

    private ScoutTerrain? SurveyTerrain(CellAddress cell)
    {
        if (!terrain.TryGetValue(cell, out var t)) return surveyTerrain?.Invoke(cell);
        return new(t.Terrain == "water", t.Water.DistanceToRiver == 0, t.ElevationMeters, t.TemperatureC, t.Moisture,
            t.NaturalState.ForestBiomass, new[] { "forage", "game", "fish" }.Sum(pool => Capacity(t, pool) * RecoveryRate(pool) * WeatherGrowth(cell)));
    }

    private void EvaluateSupply(CityState city)
    {
        if (Rules.Exploration is not { } rule || State.Cities[city.Id].Supply is not { } supply) return;
        var life = State.Cities[city.Id]; var need = Population(city) * city.FoodPerPersonPerDay;
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
        var expedition = State.Scouting?.Expeditions.FirstOrDefault(e => e.CityId == city.Id && ActiveScout(e));
        var last = State.Scouting?.Expeditions.FirstOrDefault(e => e.CityId == city.Id);
        supply.Action = expedition is not null ? (expedition.Phase == "returning" ? "Разведчики возвращаются с наблюдениями" : "Идёт обследование неизвестного направления") :
            last?.Phase == "lost" && world.Day - last.LostDay.GetValueOrDefault(world.Day) < 30 ? "Группа не вернулась в ожидаемый срок; сведений о её судьбе нет" :
            !IsForager ? "Оценка природного снабжения; промышленный сценарий не отправляет разведчиков" :
            supply.PressureStreak < rule.PressureDays ? (reason is null ? "Расширение пока не требуется" : "Проверяем, устойчиво ли ухудшение") :
            world.Day - supply.LastDepartureDay < rule.CooldownDays ? "Повторная разведка после паузы; проверяем полученные сведения" :
            Rules.Decisions is not null && ApprovedScoutDecision(city) is null ? "Обсуждается сбор разведывательной группы" :
            PlanExpedition(city, $"plan:{city.Id}:{State.Scouting?.NextId}") is not { } plan ? "Не хватает переносимой вместимости для четырёхдневного похода" :
            LaunchBlock(city, plan) ?? "Готовится разведка на следующий день";
    }

    private bool HasTransportAnimal(CityState city)
    {
        var herds = State.Cities[city.Id].Biology?.Herds;
        return herds is not null && new[] { "horse", "deer", "cow" }.Any(id => herds.GetValueOrDefault(id)?.Count > 0);
    }

    private ScoutPlan? PlanExpedition(CityState city, string key)
    {
        var rule = Rules.Exploration!;
        var pack = Knows(city, "draught_animals") && HasTransportAnimal(city);
        var riding = Knows(city, "riding") && HasTransportAnimal(city);
        var capacity = rule.PartySize * rule.BaseCarryTonnesPerPerson * (pack ? rule.PackAnimalCapacityMultiplier : 1);
        var raftReady = Knows(city, "rafts") && city.Stocks.GetValueOrDefault("timber") >= rule.RaftTimberTonnes;
        var raftTimber = raftReady ? rule.RaftTimberTonnes : 0;
        var daily = rule.PartySize * (city.FoodPerPersonPerDay + WaterPerPerson());
        var maximum = Math.Min(rule.MaximumProvisionDays, (int)Math.Floor((capacity - raftTimber) / Math.Max(1e-9, daily)));
        if (maximum < rule.MinimumProvisionDays && raftReady)
        {
            raftReady = false; raftTimber = 0;
            maximum = Math.Min(rule.MaximumProvisionDays, (int)Math.Floor(capacity / Math.Max(1e-9, daily)));
        }
        if (maximum < rule.MinimumProvisionDays) return null;
        var nominal = rule.MinimumProvisionDays + (int)Math.Floor(StableRoll(key, 0, 11) * (rule.MaximumProvisionDays - rule.MinimumProvisionDays + 1));
        var days = Math.Clamp(nominal, rule.MinimumProvisionDays, maximum);
        return new(days, Math.Max(2, days / 2), capacity,
            rule.PartySize * city.FoodPerPersonPerDay * days, rule.PartySize * WaterPerPerson() * days,
            raftReady, raftTimber, riding ? rule.RidingSpeedMultiplier : 1);
    }

    private string? LaunchBlock(CityState city, ScoutPlan plan)
    {
        var r = Rules.Exploration!; var available = State.Cities[city.Id].LaborAvailableHours;
        if (surveyTerrain is null) return "Нет доступа к неизученной местности";
        if (r.PartySize * Rules.WorkHoursPerDay > available * r.MaximumLaborShare) return "Недостаточно свободных работников для разведки";
        if (city.Stocks["food"] < plan.Food + Population(city) * city.FoodPerPersonPerDay * r.HomeReserveDays || city.Stocks["water"] < plan.Water)
            return "Сначала нужен запас пищи и воды для похода";
        if (plan.RaftReady && city.Stocks.GetValueOrDefault("timber") < plan.RaftTimber) return "Не хватает древесины для походного плота";
        return null;
    }

    private double WaterPerPerson() => content.Resources.Resources.Single(r => r.Id == "water").HouseholdNeed!.PerPersonPerDay;

    private CollectiveProposal? ApprovedScoutDecision(CityState city) => State.Cities[city.Id].Council?.Proposals
        .Where(p => p.Kind == "scouting" && p.Phase == "approved").OrderBy(p => p.CreatedDay).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();

    private double RunScouting(CityState city, DailyTelemetry telemetry)
    {
        if (Rules.Exploration is not { } rule || State.Scouting is not { } scouting) return 0;
        var life = State.Cities[city.Id]; var supply = life.Supply!;
        supply.ScoutPeopleToday = 0; supply.ScoutLaborHours = 0;
        var expedition = scouting.Expeditions.FirstOrDefault(e => e.CityId == city.Id && ActiveScout(e));
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
        var mandate = Rules.Decisions is null ? null : ApprovedScoutDecision(city);
        if (expedition is null && IsForager && supply.PressureStreak >= rule.PressureDays &&
            world.Day - supply.LastDepartureDay >= rule.CooldownDays && (Rules.Decisions is null || mandate is not null))
        {
            var planKey = mandate?.Id ?? $"plan:{city.Id}:{scouting.NextId}";
            var plan = PlanExpedition(city, planKey);
            if (plan is not null && LaunchBlock(city, plan) is null)
            {
                var home = Anchor(city); var directionCell = mandate?.Sites.FirstOrDefault(s => s.Id == mandate.SelectedSite)?.Cell ?? ScoutDirection(city, scouting.NextId, plan.RaftReady);
                var origin = topology.ToUnitVector(home); var direction = topology.ToUnitVector(directionCell);
                if (Math.Abs(direction.Dot(origin) - 1) < 1e-12)
                {
                    var alternatives = topology.GetNeighbors(home).Where(cell => plan.RaftReady || SurveyTerrain(cell) is { Water: false }).ToArray();
                    directionCell = (alternatives.Length > 0 ? alternatives : topology.GetNeighbors(home).ToArray())
                        .OrderBy(cell => SphericalSimulation.ZoneId(cell), StringComparer.Ordinal).First();
                    direction = topology.ToUnitVector(directionCell);
                }
                var councilDirection = UnitVector3.Normalize(direction.X - origin.X, direction.Y - origin.Y, direction.Z - origin.Z);
                expedition = new ScoutExpedition
                {
                    Id = $"scout-{scouting.NextId++:000000}", CityId = city.Id, Home = home, Current = home,
                    Direction = councilDirection,
                    People = rule.PartySize, InitialPeople = rule.PartySize, DepartureDay = world.Day, Reason = supply.Reason, Path = [home], LastLeg = [home],
                    ProvisionDays = plan.ProvisionDays, PlannedOutboundDays = plan.OutboundDays, CargoCapacity = plan.Capacity,
                    Food = plan.Food, Water = plan.Water, RaftReady = plan.RaftReady, RaftTimber = plan.RaftTimber,
                    SpeedMultiplier = plan.SpeedMultiplier, DecisionId = mandate?.Id
                };
                if (Rules.Wellbeing is not null)
                {
                    var foodStock = life.Wellbeing!.FoodStock; foodStock.Reconcile(city.Stocks["food"]);
                    expedition.ProvisionComposition = new() { Amounts = foodStock.Take(expedition.Food) };
                }
                city.Stocks["food"] -= expedition.Food; city.Stocks["water"] -= expedition.Water;
                if (expedition.RaftReady) city.Stocks["timber"] -= expedition.RaftTimber;
                var recent = scouting.RecentDirections.GetValueOrDefault(city.Id);
                if (recent is null) scouting.RecentDirections[city.Id] = recent = [];
                recent.Add(councilDirection); while (recent.Count > 6) recent.RemoveAt(0);
                scouting.Expeditions.RemoveAll(e => e.CityId == city.Id);
                scouting.Expeditions.Add(expedition); supply.LastDepartureDay = world.Day;
                if (mandate is not null) CollectiveDecisions.MarkStarted(mandate, expedition.Id, world.Day);
                var causes = new[] { supply.PressureEventId, mandate?.CauseEventId }.Where(id => id is not null).Cast<string>().ToArray();
                expedition.CauseEventId = Journal.Record(world, "scouting_departed", city.Id, causes, new JsonObject
                {
                    ["cityId"] = city.Id, ["expeditionId"] = expedition.Id, ["people"] = expedition.People,
                    ["reason"] = expedition.Reason, ["provisionDays"] = expedition.ProvisionDays,
                    ["capacityTonnes"] = expedition.CargoCapacity, ["raftReady"] = expedition.RaftReady,
                    ["speedMultiplier"] = expedition.SpeedMultiplier,
                    ["targetSector"] = SphericalSimulation.ZoneId(directionCell)
                }).Id;
            }
        }
        if (expedition is null) return 0;

        var foodNeed = expedition.People * city.FoodPerPersonPerDay;
        var waterNeed = expedition.People * WaterPerPerson();
        var foodCoverage = ConsumeProvision(city.Id, "food", foodNeed, expedition.Food, value => expedition.Food = value);
        expedition.ProvisionComposition?.Reconcile(expedition.Food);
        var waterCoverage = ConsumeProvision(city.Id, "water", waterNeed, expedition.Water, value => expedition.Water = value);
        if (ApplyScoutHazards(city, expedition, foodCoverage, waterCoverage)) return 0;

        var hours = Math.Min(life.LaborAvailableHours, expedition.People * Rules.WorkHoursPerDay);
        supply.ScoutPeopleToday = expedition.People; supply.ScoutLaborHours = hours;
        expedition.LastStepDay = world.Day; expedition.LastLeg = [expedition.Current];
        if (world.Day - expedition.DepartureDay >= expedition.PlannedOutboundDays + expedition.ExtensionDays)
        {
            if (CanExtend(expedition, city, rule)) expedition.ExtensionDays++;
            else StartReturn(expedition);
        }
        var perPersonHours = hours / expedition.People;
        var stepLimit = Math.Min(64, (int)Math.Ceiling(rule.StepsPerDay * expedition.SpeedMultiplier));
        for (var step = 0; step < stepLimit && perPersonHours > 0; step++)
        {
            if (expedition.Phase == "returning" && expedition.ReturnIndex == 0) { CompleteSurvey(city, expedition, telemetry); break; }
            CellAddress? next;
            if (expedition.Phase == "returning") next = expedition.Path[expedition.ReturnIndex - 1];
            else next = NextScoutCell(expedition);
            if (next is null) { StartReturn(expedition); continue; }
            var currentTerrain = SurveyTerrain(expedition.Current); var nextTerrain = SurveyTerrain(next.Value);
            if (currentTerrain is null || nextTerrain is null || !CanTraverse(expedition, currentTerrain, nextTerrain)) break;
            var waterTravel = currentTerrain.Water || nextTerrain.Water;
            var speed = expedition.SpeedMultiplier * (waterTravel ? rule.RaftSpeedMultiplier : 1);
            var travelHours = (waterTravel ? 1 : 1 + nextTerrain.Forest * .6 + Math.Abs(nextTerrain.Elevation - currentTerrain.Elevation) / 100) *
                (waterTravel ? 1 : 1 - trailStrength.GetValueOrDefault((expedition.Current, next.Value)) * trailRules.MaximumCostReduction) *
                WeatherWalking(next.Value) * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / speed;
            var cost = travelHours + (expedition.Phase == "outbound" ? rule.SurveyHoursPerCell : 0);
            if (cost > perPersonHours) break;
            perPersonHours -= cost;
            if (!waterTravel) Passage([expedition.Current, next.Value], expedition.People);
            expedition.Current = next.Value; expedition.LastLeg.Add(next.Value); expedition.TravelMode = nextTerrain.Water ? "raft" : "foot";
            if (nextTerrain.Water && expedition.RaftTimber > 0) expedition.RaftTimber = 0;
            if (expedition.Phase == "returning") expedition.ReturnIndex--;
            else
            {
                expedition.Path.Add(next.Value);
                ObserveScoutCell(city, expedition, next.Value, nextTerrain, rule, telemetry);
            }
        }
        if (expedition.Phase == "returning" && expedition.ReturnIndex == 0) CompleteSurvey(city, expedition, telemetry);
        return hours;
    }

    private CellAddress ScoutDirection(CityState city, int sequence, bool canUseWater)
    {
        var home = Anchor(city); var known = State.Scouting!.KnownCells.GetValueOrDefault(city.Id) ?? [];
        var origin = topology.ToUnitVector(home);
        var reference = Math.Abs(origin.Z) < .82 ? new UnitVector3(0, 0, 1) : new UnitVector3(0, 1, 0);
        var east = UnitVector3.Normalize(reference.Y * origin.Z - reference.Z * origin.Y,
            reference.Z * origin.X - reference.X * origin.Z, reference.X * origin.Y - reference.Y * origin.X);
        var north = UnitVector3.Normalize(origin.Y * east.Z - origin.Z * east.Y,
            origin.Z * east.X - origin.X * east.Z, origin.X * east.Y - origin.Y * east.X);
        var recent = State.Scouting.RecentDirections.GetValueOrDefault(city.Id) ?? [];
        var offset = (int)Math.Floor(StableRoll(city.Id + ":sector", 0, 0) * 12) % 12;
        var candidates = new List<(CellAddress Cell, UnitVector3 Bearing, int Unknown, double Repeat, double Tie)>();
        for (var index = 0; index < 12; index++)
        {
            var sector = (index + offset) % 12; var angle = sector * Math.PI * 2 / 12;
            var bearing = UnitVector3.Normalize(east.X * Math.Cos(angle) + north.X * Math.Sin(angle),
                east.Y * Math.Cos(angle) + north.Y * Math.Sin(angle), east.Z * Math.Cos(angle) + north.Z * Math.Sin(angle));
            var current = home; var unknown = 0; var rayVisited = new HashSet<CellAddress> { home };
            for (var step = 0; step < 8; step++)
            {
                var steps = topology.GetNeighbors(current).Where(cell => !rayVisited.Contains(cell) && (canUseWater || SurveyTerrain(cell) is { Water: false }))
                    .Select(cell => (Cell: cell, Alignment: StepAlignment(current, cell, bearing)))
                    .OrderByDescending(item => item.Alignment).ThenBy(item => SphericalSimulation.ZoneId(item.Cell), StringComparer.Ordinal).ToArray();
                if (steps.Length == 0 || steps[0].Cell == current) break;
                current = steps[0].Cell; rayVisited.Add(current); if (!known.Contains(SphericalSimulation.ZoneId(current))) unknown++;
            }
            // Recency-weighted pressure avoids bursts of the same sector after
            // all coarse land bearings have been visited at least once.
            var repeat = recent.Select((item, age) => Math.Max(0, item.Dot(bearing)) * (age + 1)).Sum();
            if (recent.LastOrDefault().Dot(bearing) > .75) repeat += 100;
            candidates.Add((current, bearing, unknown, repeat, StableRoll(city.Id + ":direction", sequence, sector)));
        }
        return candidates.OrderBy(item => item.Repeat).ThenByDescending(item => item.Unknown).ThenBy(item => item.Tie)
            .ThenBy(item => SphericalSimulation.ZoneId(item.Cell), StringComparer.Ordinal).First().Cell;
    }

    private CellAddress? NextScoutCell(ScoutExpedition expedition)
    {
        var known = State.Scouting!.KnownCells.GetValueOrDefault(expedition.CityId) ?? [];
        var visited = expedition.Path.ToHashSet();
        var biology = State.Cities[expedition.CityId].Biology;
        var choices = topology.GetNeighbors(expedition.Current).Where(c => !visited.Contains(c) && SurveyTerrain(c) is { } site &&
                (!site.Water || expedition.RaftReady && IsCoastalWater(c)))
            .Select(c =>
            {
                var site = SurveyTerrain(c)!; var plants = BiologyRules is null ? [] : WildCrops(c).Select(item => item.Id).ToArray();
                var animals = ScoutAnimals(c); var interest = Math.Min(.14, site.FoodRenewalPerDay * 450);
                var reason = "неизведанная местность";
                if (site.FreshWater && expedition.Water < expedition.CargoCapacity * .3) { interest += .42; reason = "источник пресной воды"; }
                var unknownPlants = plants.Count(id => biology?.KnownPlants.Contains(id) != true && !expedition.Observations.Any(o => o.Plants?.Contains(id) == true));
                if (unknownPlants > 0) { interest += Math.Min(.34, unknownPlants * .12); reason = "неизвестная растительность"; }
                var unknownAnimals = animals.Count(id => biology?.KnownAnimals.Contains(id) != true && !expedition.Observations.Any(o => o.Animals?.Contains(id) == true));
                if (animals.Length > 0) { interest += .12 + Math.Min(.36, unknownAnimals * .16); reason = unknownAnimals > 0 ? "следы неизвестных животных" : "стадо или промысловая живность"; }
                var alignment = StepAlignment(expedition.Current, c, expedition.Direction);
                var novelty = known.Contains(SphericalSimulation.ZoneId(c)) ? 0 : .16;
                return (Cell: c, Score: alignment + interest + novelty + StableRoll(expedition.Id + ":interest", world.Day, c.X * 997 + c.Y) * .015, Reason: reason);
            }).OrderByDescending(item => item.Score).ThenBy(item => SphericalSimulation.ZoneId(item.Cell), StringComparer.Ordinal).ToArray();
        if (choices.Length == 0) return null;
        expedition.CurrentInterest = choices[0].Reason;
        return choices[0].Cell;
    }

    private double StepAlignment(CellAddress from, CellAddress to, UnitVector3 bearing)
    {
        var a = topology.ToUnitVector(from); var b = topology.ToUnitVector(to);
        return UnitVector3.Normalize(b.X - a.X, b.Y - a.Y, b.Z - a.Z).Dot(bearing);
    }

    private bool IsCoastalWater(CellAddress cell) => topology.GetNeighbors(cell).Any(c => SurveyTerrain(c) is { Water: false });
    private static bool CanTraverse(ScoutExpedition expedition, ScoutTerrain _, ScoutTerrain next) =>
        !next.Water || expedition.RaftReady;

    private void ObserveScoutCell(CityState city, ScoutExpedition expedition, CellAddress cell, ScoutTerrain observed,
        SettlementExplorationRules rule, DailyTelemetry telemetry)
    {
        var plants = BiologyRules is null ? [] : WildCrops(cell).Select(c => c.Id).ToArray();
        var animals = ScoutAnimals(cell);
        var observedClaim = world.Spatial.Territories.GetValueOrDefault(SphericalSimulation.ZoneId(cell))?.AssignedCityId;
        if (observedClaim == city.Id) observedClaim = null;
        expedition.Observations.Add(new(cell, world.Day, observed.FreshWater, observed.FoodRenewalPerDay, plants, animals, observedClaim));
        var biology = State.Cities[city.Id].Biology;
        foreach (var plant in plants.Where(id => biology?.KnownPlants.Contains(id) != true))
        {
            var sample = Math.Min(.00005, CargoFree(expedition));
            if (sample <= 0) break;
            expedition.SeedSamples[plant] = expedition.SeedSamples.GetValueOrDefault(plant) + sample;
        }
        TryCaptureAnimal(city, expedition, cell, animals, rule);
        var added = false;
        if (observed.FreshWater && CargoFree(expedition) > 0)
        {
            var fill = Math.Min(CargoFree(expedition), Math.Max(0, expedition.CargoCapacity * .45 - expedition.Water));
            if (fill > 0) { expedition.Water += fill; expedition.RefilledWater += fill; Add(telemetry.ProductionByResource, "water", fill); added = true; }
        }
        if (plants.Any(id => biology?.KnownPlants.Contains(id) == true) && CargoFree(expedition) > 0)
        {
            var forage = Math.Min(CargoFree(expedition), observed.FoodRenewalPerDay * rule.ResupplyShare);
            if (forage > 0) { expedition.Food += forage; expedition.ForagedFood += forage; Add(telemetry.ProductionByResource, "food", forage); added = true; expedition.ProvisionComposition?.Reconcile(expedition.Food); }
        }
        if (added) expedition.LastResupplyDay = world.Day;
    }

    private string[] ScoutAnimals(CellAddress cell)
    {
        var actual = wildlifeIndex.GetValueOrDefault(cell)?.Select(item => item.Group.SpeciesId).Where(id => id is not null).Cast<string>() ?? [];
        return actual.Append(WildAnimal(cell)).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private void TryCaptureAnimal(CityState city, ScoutExpedition expedition, CellAddress cell, IReadOnlyList<string> observed, SettlementExplorationRules rule)
    {
        if (BiologyRules is null || !wildlifeIndex.TryGetValue(cell, out var groups)) return;
        foreach (var species in observed.Where(expedition.CaptureAttempts.Add))
        {
            var animal = BiologyRules.Animals.FirstOrDefault(a => a.Id == species); if (animal is null || CargoFree(expedition) < animal.BodyTonnes) continue;
            var group = groups.Select(p => p.Group).FirstOrDefault(g => g.SpeciesId == species && g.Biomass >= animal.BodyTonnes); if (group is null) continue;
            var speciesDifficulty = Math.Clamp(Math.Sqrt(.08 / animal.BodyTonnes) * 24 / animal.CaptureHours, .25, 2);
            var chance = Math.Min(.9, rule.BaseLiveCaptureChance * speciesDifficulty * (Knows(city, "taming") ? rule.TamingCaptureMultiplier : 1));
            if (StableRoll(expedition.Id + ":capture:" + species, world.Day, expedition.Path.Count) >= chance) continue;
            group.Biomass -= animal.BodyTonnes; group.Harvested += animal.BodyTonnes; group.Alert = Math.Min(10, group.Alert + .5);
            group.Threat = expedition.Current; group.LastHuntedDay = world.Day;
            expedition.CapturedAnimals[species] = expedition.CapturedAnimals.GetValueOrDefault(species) + 1;
        }
    }

    private double CapturedMass(ScoutExpedition expedition) => BiologyRules is null ? 0 : expedition.CapturedAnimals.Sum(pair =>
        pair.Value * (BiologyRules.Animals.FirstOrDefault(a => a.Id == pair.Key)?.BodyTonnes ?? 0));
    private double CargoFree(ScoutExpedition expedition) => Math.Max(0, expedition.CargoCapacity - expedition.CargoUsed - CapturedMass(expedition));

    private bool CanExtend(ScoutExpedition expedition, CityState city, SettlementExplorationRules rule)
    {
        if (expedition.ExtensionDays >= rule.MaximumExtensionDays || expedition.LastResupplyDay < world.Day - 1) return false;
        var returnDays = Math.Max(1, (int)Math.Ceiling((expedition.Path.Count - 1d) / Math.Max(1, rule.StepsPerDay * expedition.SpeedMultiplier)));
        return expedition.Food >= expedition.People * city.FoodPerPersonPerDay * (returnDays + 1) &&
            expedition.Water >= expedition.People * WaterPerPerson() * Math.Min(returnDays + 1, 3);
    }

    private bool ApplyScoutHazards(CityState city, ScoutExpedition expedition, double foodCoverage, double waterCoverage)
    {
        var site = SurveyTerrain(expedition.Current); if (site is null) return false;
        var rule = Rules.Exploration!;
        var exposure = rule.BaseFatalityChancePerDay + Math.Max(0, 1 - foodCoverage) * .035 + Math.Max(0, 1 - waterCoverage) * .18 +
            Math.Max(0, -site.Temperature - 5) * .001 + Math.Max(0, site.Elevation - 1200) / 200000 +
            (site.Water && expedition.TravelMode == "raft" ? .0015 : 0);
        expedition.HazardExposure += exposure;
        if (StableRoll(expedition.Id + ":hazard", world.Day, expedition.People) >= Math.Min(.75, exposure)) return false;
        expedition.People--; expedition.Casualties++;
        Journal.Record(world, expedition.People > 0 ? "scouting_casualty" : "scouting_lost", city.Id, [expedition.CauseEventId], new JsonObject
        {
            ["cityId"] = city.Id, ["expeditionId"] = expedition.Id, ["remaining"] = expedition.People,
            ["foodCoverage"] = foodCoverage, ["waterCoverage"] = waterCoverage, ["exposure"] = exposure
        });
        if (expedition.People > 0) { StartReturn(expedition); return false; }
        expedition.Phase = "lost"; expedition.LostDay = world.Day; expedition.LastStepDay = world.Day; expedition.LastLeg = [expedition.Current];
        return true;
    }

    private static double StableRoll(string key, int day, int salt)
    {
        uint hash = 2166136261;
        foreach (var c in key) hash = unchecked((hash ^ c) * 16777619);
        hash = unchecked((hash ^ (uint)day) * 16777619); hash = unchecked((hash ^ (uint)salt) * 16777619);
        hash ^= hash >> 16; hash *= 0x7feb352d; hash ^= hash >> 15; hash *= 0x846ca68b; hash ^= hash >> 16;
        return hash / (double)uint.MaxValue;
    }

    private double ConsumeProvision(string city, string resource, double need, double available, Action<double> set)
    {
        var consumed = Math.Min(need, available); set(Math.Max(0, available - consumed));
        remoteNeeds[(city, resource)] = need; remoteConsumption[(city, resource)] = consumed;
        return need <= 0 ? 1 : consumed / need;
    }

    private static void StartReturn(ScoutExpedition expedition)
    {
        if (expedition.Phase != "outbound") return;
        expedition.Phase = "returning"; expedition.ReturnIndex = expedition.Path.Count - 1;
    }

    private void CompleteSurvey(CityState city, ScoutExpedition expedition, DailyTelemetry telemetry)
    {
        if (expedition.Phase == "returned") return;
        expedition.Phase = "returned"; expedition.ReturnDay = world.Day; expedition.Current = expedition.Home; expedition.TravelMode = "foot";
        var returnedFood = expedition.Food; var returnedWater = expedition.Water;
        if (Rules.Wellbeing is not null && expedition.ProvisionComposition is { } composition)
        {
            var stock = State.Cities[city.Id].Wellbeing!.FoodStock; stock.Reconcile(city.Stocks["food"]);
            composition.Reconcile(expedition.Food);
            foreach (var item in composition.Take(expedition.Food)) stock.Add(item.Key, item.Value);
        }
        city.Stocks["food"] += expedition.Food; city.Stocks["water"] += expedition.Water;
        expedition.Food = expedition.Water = 0; expedition.ProvisionComposition?.Reconcile(0);
        var known = State.Scouting!.KnownCells[city.Id];
        var unknown = expedition.Observations.Where(o => known.Add(SphericalSimulation.ZoneId(o.Cell))).ToArray();
        var plants = unknown.SelectMany(o => o.Plants ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var animals = unknown.SelectMany(o => o.Animals ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var foreignClaims = unknown.Where(o => o.ObservedClaim is not null).GroupBy(o => o.ObservedClaim!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var biology = State.Cities[city.Id].Biology;
        if (biology is not null)
        {
            biology.KnownPlants.UnionWith(plants); biology.KnownAnimals.UnionWith(animals);
            foreach (var (species, amount) in expedition.SeedSamples)
            {
                var crop = BiologyRules?.Crops.FirstOrDefault(c => c.Id == species); if (crop is null) continue;
                city.Stocks[crop.SeedResource] += amount; Add(telemetry.ProductionByResource, crop.SeedResource, amount);
                Add(State.Cities[city.Id].PracticeHours, "seed:" + species, amount / .00005 * Rules.Exploration!.SurveyHoursPerCell);
            }
            foreach (var (species, count) in expedition.CapturedAnimals)
            {
                if (!Knows(city, "taming")) { Add(State.Cities[city.Id].PracticeHours, "hunt", count * 4); continue; }
                if (!biology.Herds.TryGetValue(species, out var herd)) biology.Herds[species] = herd = new();
                for (var i = 0; i < count; i++) { if ((herd.Captured + i) % 2 == 0) herd.Females++; else herd.Males++; }
                herd.Captured += count; herd.Health = Math.Max(.55, herd.Health);
            }
        }
        var candidates = unknown.Where(o => o.FreshWater && (o.FoodRenewalPerDay > 0 || (o.Plants?.Count ?? 0) > 0))
            .OrderByDescending(o => o.FoodRenewalPerDay).ThenBy(o => SphericalSimulation.ZoneId(o.Cell), StringComparer.Ordinal).Take(5).ToArray();
        var outcome = candidates.Length > 0 ? "Найдены участки у пресной воды и новые природные сведения" :
            unknown.Length > 0 ? "Направление обследовано, пригодный участок у воды не найден" : "Не удалось выйти за пределы знакомой окрестности";
        var supply = State.Cities[city.Id].Supply!;
        supply.Reports.Add(new(expedition.Id, expedition.DepartureDay, world.Day, unknown.Length, candidates, outcome,
            plants, animals, expedition.CapturedAnimals.ToDictionary(), expedition.Casualties, foreignClaims));
        while (supply.Reports.Count > Rules.Exploration!.MaximumReports) supply.Reports.RemoveAt(0);
        if (expedition.DecisionId is { } decisionId && State.Cities[city.Id].Council?.Proposals.FirstOrDefault(p => p.Id == decisionId) is { } proposal)
        { proposal.Phase = "observing"; proposal.FinishedDay = world.Day; }
        Journal.Record(world, "scouting_returned", city.Id, [expedition.CauseEventId], new JsonObject
        {
            ["cityId"] = city.Id, ["expeditionId"] = expedition.Id, ["surveyedCells"] = unknown.Length,
            ["candidates"] = candidates.Length, ["plants"] = plants.Length, ["animals"] = animals.Length,
            ["durationDays"] = world.Day - expedition.DepartureDay,
            ["routeCells"] = Math.Max(0, expedition.Path.Count - 1),
            ["territorySample"] = System.Text.Json.JsonSerializer.SerializeToNode(unknown.Take(8).Select(o => SphericalSimulation.ZoneId(o.Cell)).ToArray()),
            ["plantIds"] = System.Text.Json.JsonSerializer.SerializeToNode(plants),
            ["animalIds"] = System.Text.Json.JsonSerializer.SerializeToNode(animals),
            ["foreignClaims"] = System.Text.Json.JsonSerializer.SerializeToNode(foreignClaims),
            ["seedSamples"] = System.Text.Json.JsonSerializer.SerializeToNode(expedition.SeedSamples),
            ["capturedBySpecies"] = System.Text.Json.JsonSerializer.SerializeToNode(expedition.CapturedAnimals),
            ["capturedAnimals"] = expedition.CapturedAnimals.Values.Sum(), ["casualties"] = expedition.Casualties,
            ["returnedFoodKg"] = returnedFood * 1000, ["returnedWaterLitres"] = returnedWater * 1000,
            ["foragedFoodKg"] = expedition.ForagedFood * 1000, ["refilledWaterLitres"] = expedition.RefilledWater * 1000,
            ["outcome"] = outcome
        });
    }
}
