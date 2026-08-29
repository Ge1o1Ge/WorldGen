using System.Text.Json.Nodes;
using WorldGen.Core.Content;
using WorldGen.Core.Settlements;
using WorldGen.Core.Spatial;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

/// <summary>Daily household batches, not individual pathfinding agents. All work shares
/// the industrial labor budget; all extraction uses the same finite natural stocks.</summary>
public sealed partial class SettlementSimulation
{
    private readonly WorldState world;
    private readonly ContentCatalog content;
    private readonly CubeSphereTopology topology;
    private readonly SphericalSettlementLayer layer;
    private readonly IReadOnlyDictionary<string, CellAddress> addresses;
    private readonly Dictionary<CellAddress, Territory> terrain;
    private readonly Dictionary<string, NaturalPoolRule> pools;
    private readonly Dictionary<CellAddress, RouteTree> routes = new();
    private readonly Dictionary<string, LocalTrailState> edges = new(StringComparer.Ordinal);
    private readonly Dictionary<(CellAddress, CellAddress), double> trailStrength = new();
    private readonly SettlementTrailRules trailRules;
    private readonly Dictionary<CellAddress, double> waterTaken = new();
    private int routingDay = -1;
    private readonly Func<CellAddress,Territory>? materialize;
    public SettlementRules Rules { get; }
    public SettlementDevelopmentState State => world.SettlementDevelopment!;
    public bool IsForager => State.IsForager;

    public SettlementSimulation(WorldState world, ContentCatalog content, SettlementRules rules, CubeSphereTopology topology,
        SphericalSettlementLayer layer, IReadOnlyDictionary<string, CellAddress> addresses, bool foragers = false,
        Func<CellAddress, ScoutTerrain>? surveyTerrain = null, SphericalTerrainGenerator? planetTerrain = null, Func<CellAddress,Territory>? materialize = null)
    {
        this.world = world; this.content = content; Rules = rules; this.topology = topology; this.layer = layer; this.addresses = addresses;
        trailRules = rules.Trails ?? new SettlementTrailRules();
        terrain = addresses.ToDictionary(pair => pair.Value, pair => world.Spatial.Territories[pair.Key]);
        pools = rules.NaturalPools.ToDictionary(x => x.Id, StringComparer.Ordinal);
        this.surveyTerrain = surveyTerrain;
        this.planetTerrain = planetTerrain;
        this.materialize=materialize;
        if (world.SettlementDevelopment is null)
        {
            world.SettlementDevelopment = new() { IsForager = foragers };
            foreach (var city in world.Cities.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                State.Cities[city.Id] = new();
                if (rules.Primitive is { } era)
                {
                    State.Cities[city.Id].Discoveries.UnionWith(era.Technologies.Where(t => city.TechnologyState[t.Id].Knowledge >= 1).Select(t => t.Id));
                    State.Cities[city.Id].Primitive = new() { HerdBiomass = era.Biosphere is null && State.Cities[city.Id].Discoveries.Contains("taming") ? Population(city) * .002 : 0 };
                }
                if (!IsForager) State.Cities[city.Id].Discoveries.UnionWith(rules.Discoveries.Select(x => x.Id));
                var count = (int)Math.Ceiling(Population(city) / (double)rules.ResidentsPerHouse);
                var anchor = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
                for (var i = 0; i < count; i++)
                {
                    var site = FindBuildingSite(city, anchor);
                    if (site is null) break;
                    AddBuilding(city, "house", site.Value, initial: true);
                }
            }
            RehousePopulation();
        }
        else
        {
            if (State.SchemaVersion != 1) throw new InvalidOperationException("Неизвестная версия состояния домохозяйств");
            foreach (var building in State.Buildings) PlaceInRegistry(building);
        }
        foreach (var edge in State.Trails) edges[EdgeKey(edge.From, edge.To)] = edge;
        RebuildTrailStrength();
        SettlementInformation.Initialize(world, addresses);
        if (rules.Exploration is not null)
        {
            State.Scouting ??= new();
            foreach (var life in State.Cities.Values) life.Supply ??= new();
        }
        if (rules.Decisions is not null)
            foreach (var life in State.Cities.Values) life.Council ??= new();
        if (rules.Subsistence is not null)
        {
            State.HarvestPressure ??= new(StringComparer.Ordinal);
            InitializeWildlife();
        }
        foreach (var building in State.Buildings) InitializeLifecycle(building, baseline: true);
        InitializeWellbeing();
        InitializePrimitiveWorld();
        InitializeBiology();
        if (rules.Storage is not null)
            foreach (var life in State.Cities.Values) life.Storage ??= new();
    }

    public void RehousePopulation()
    {
        foreach (var city in world.Cities.Values)
        {
            var total = Population(city);
            var houses = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house").ToArray();
            // Reconcile births/deaths/unhoused people, not the order of buildings.
            // Occupants stay where they live until an explicit move is paid for.
            var remaining = total;
            foreach (var house in houses)
            {
                house.Residents = house.Status == "active" ? Math.Min(Math.Clamp(house.Residents, 0, Rules.ResidentsPerHouse), remaining) : 0;
                remaining -= house.Residents;
            }
            foreach (var house in houses.Where(h => h.Status == "active" && !Moving(h) && h.Lifecycle?.Retiring != true))
            {
                var arriving = Math.Min(remaining, Rules.ResidentsPerHouse - house.Residents);
                TransferWellbeing(city.Id, $"camp:{city.Id}", HouseholdIdentity(house.Id), arriving, house.Residents);
                house.Residents += arriving; remaining -= arriving;
            }
            foreach (var territory in terrain.Values.Where(t => t.AssignedCityId == city.Id)) territory.Population = 0;
            foreach (var house in houses) terrain[house.Cell].Population += house.Residents;
            var life = State.Cities[city.Id];
            life.HousingCapacity = State.Buildings.Count(b => b.CityId == city.Id && b.Kind == "house" && b.Status == "active") * Rules.ResidentsPerHouse;
            life.Unhoused = remaining;
            // Unhoused people remain real people, counted at the camp rather than deleted.
            world.Spatial.Territories[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!].Population += remaining;
            ReconcileWellbeingHomes(city);
        }
        SpatialRuntime.RecalculateAggregates(world);
    }

    public void RecoverNaturalSites()
    {
        AdvancePrimitiveWeather();
        RecoverHarvestPressure();
        AdvanceWildlife();
        AgeBuildingsAndRechargeWells();
        var cultivated = State.Buildings.Where(b => b.Kind == "garden" && Standing(b)).Select(b => b.Cell).ToHashSet();
        var meadow = Rules.Lifecycle is { } fieldRules ? State.Buildings.Where(b => b.Field?.FallowSinceDay is { } day && world.Day - day < fieldRules.MeadowRecoveryDays).Select(b => b.Cell).ToHashSet() : [];
        foreach (var territory in terrain.Values)
        {
            var natural = territory.NaturalState;
            if (Rules.Lifecycle is null || !cultivated.Contains(addresses[territory.Id]))
                natural.SoilQuality += (territory.Fertility - natural.SoilQuality) * (Rules.Lifecycle?.FallowRecoveryPerDay ?? .00045);
            foreach (var id in new[] { "timber", "fish" })
            {
                if (id == "timber" && (cultivated.Contains(addresses[territory.Id]) || meadow.Contains(addresses[territory.Id]))) continue;
                var rule = pools[id]; var value = Stock(territory, id);
                var capacity = Capacity(territory, id);
                if (id == "timber" && Rules.Subsistence is not null)
                    capacity *= Math.Max(0, 1 - layer.Construction.GetOccupiedCapacity(addresses[territory.Id]) / (double)layer.Construction.GetCapacity(addresses[territory.Id]));
                SetStock(territory, id, value + Math.Max(0, capacity - value) * RecoveryRate(id) * WeatherGrowth(addresses[territory.Id]));
            }
            if (!State.WildStocks.TryGetValue(territory.Id, out var stocks)) continue;
            foreach (var id in stocks.Keys.ToArray())
            {
                var rule = pools[id];
                if (rule.Renewable) stocks[id] += Math.Max(0, Capacity(territory, id) - stocks[id]) * RecoveryRate(id) * WeatherGrowth(addresses[territory.Id]);
            }
        }
    }

    private double PoolCapacity(string pool) => pools[pool].Capacity;
    public double RecoveryRate(string pool) => pools[pool].RecoveryPerDay * (Rules.Subsistence?.RecoveryScale(pool) ?? 1);
    public double Capacity(Territory t, string pool) => pool == "game" && State.Wildlife is not null ? WildlifeAt(t, capacity: true) : PoolCapacity(pool) * (pool switch
    {
        "forage" => t.Terrain == "water" ? 0 : t.Fertility * (.4 + t.ForestCover * .6),
        "game" => t.Terrain == "water" ? 0 : t.ForestCover,
        "fiber" => t.Terrain == "water" ? 0 : t.Moisture * .8,
        _ => t.ResourcePotential.GetValueOrDefault(pool)
    });

    public double Stock(Territory t, string pool) => pool switch
    {
        "game" when State.Wildlife is not null => WildlifeAt(t),
        "timber" => t.NaturalState.ForestBiomass * pools[pool].Capacity,
        "fish" => t.NaturalState.FishStock * PoolCapacity(pool),
        "clay" or "stone" or "iron_ore" => t.NaturalState.Deposits.GetValueOrDefault(pool) * Capacity(t, pool),
        _ => State.WildStocks.TryGetValue(t.Id, out var stocks) && stocks.TryGetValue(pool, out var value) ? value : Capacity(t, pool)
    };

    private void SetStock(Territory t, string pool, double amount)
    {
        amount = Math.Max(0, amount);
        if (pool == "timber") t.NaturalState.ForestBiomass = amount / pools[pool].Capacity;
        else if (pool == "fish") t.NaturalState.FishStock = amount / PoolCapacity(pool);
        else if (pool is "clay" or "stone" or "iron_ore") t.NaturalState.Deposits[pool] = amount / Math.Max(1e-9, Capacity(t, pool));
        else
        {
            if (!State.WildStocks.TryGetValue(t.Id, out var stocks)) State.WildStocks[t.Id] = stocks = new(StringComparer.Ordinal);
            stocks[pool] = amount;
        }
    }

    public double Extract(Territory t, string pool, double requested, CellAddress? hunterOrigin = null)
    {
        if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        var taken = Math.Min(requested, Stock(t, pool));
        if (pool == "game" && State.Wildlife is not null) taken = HuntWildlife(t, taken, hunterOrigin ?? addresses[t.Id]);
        else SetStock(t, pool, Stock(t, pool) - taken);
        RecordHarvestPressure(t, pool, taken);
        t.NaturalState.ExtractedBatches[pool] = t.NaturalState.ExtractedBatches.GetValueOrDefault(pool) + taken;
        return taken;
    }

    public double LimitIndustry(Territory t, RecipeDefinition recipe, double batches) =>
        recipe.SitePotential is { } pool && pools.ContainsKey(pool)
            ? Math.Min(batches, Stock(t, pool) / Math.Max(1e-9, recipe.Outputs.Values.Sum())) : batches;

    public bool UseIndustryResource(Territory t, RecipeDefinition recipe, double batches)
    {
        if (recipe.SitePotential is not { } pool || !pools.ContainsKey(pool)) return false;
        Extract(t, pool, batches * recipe.Outputs.Values.Sum()); return true;
    }

    public double ReservedWorkerDays(string cityId) => State.Cities[cityId].LaborUsedHours / Rules.WorkHoursPerDay;
    public void RecordIndustryLabor(string cityId, double workerDays) => State.Cities[cityId].IndustryLaborHours += workerDays * Rules.WorkHoursPerDay;

    public void RunDay(DailyTelemetry telemetry)
    {
        waterTaken.Clear();
        gardenTaken.Clear();
        materialChoices.Clear(); gardenSoilToday.Clear();
        if (Rules.Lifecycle is not null)
            foreach (var garden in State.Buildings.Where(ReadyGarden)) gardenSoilToday[garden.Cell] = terrain[garden.Cell].NaturalState.SoilQuality;
        remoteNeeds.Clear(); remoteConsumption.Clear();
        // Derived caches cannot affect replay: freeze routing costs for ONE world day,
        // rebuild from canonical trail strengths at the next day (also after restore).
        if (routingDay != world.Day) { routes.Clear(); routingDay = world.Day; }
        foreach (var edge in edges.Values)
        {
            var a = TrailEnvironment(edge.From); var b = TrailEnvironment(edge.To);
            edge.Strength = trailRules.Decay(edge.Strength, (a.Moisture + b.Moisture) / 2, (a.Forest + b.Forest) / 2);
            edge.Passages = 0;
        }
        foreach (var key in edges.Where(p => p.Value.Strength < trailRules.ForgetBelow).Select(p => p.Key).ToArray()) edges.Remove(key);
        RebuildTrailStrength();
        foreach (var city in world.Cities.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var life = State.Cities[city.Id]; var population = Population(city);
            life.Production.Clear(); life.Tasks.Clear(); life.WaterTravelHours = life.WaterCollected = life.LaborUsedHours = life.IndustryLaborHours = 0;
            if (Rules.Subsistence is not null) life.Food = new();
            if (Rules.Lifecycle is not null) life.Maintenance = new();
            if (Rules.Wellbeing is not null) life.Wellbeing!.ConsumedToday.Clear();
            var multiplier = city.ActiveEffects.OrderBy(p => p.Key, StringComparer.Ordinal).Aggregate(1d, (v, p) => v * p.Value.Multiplier);
            life.LaborAvailableHours = population * city.WorkerShare * multiplier * Rules.WorkHoursPerDay;
            var scoutingHours = RunScouting(city, telemetry);
            scoutingHours += RunResourceCamps(city, Math.Max(0,life.LaborAvailableHours-scoutingHours),telemetry);
            scoutingHours += SearchSeeds(city, Math.Max(0,life.LaborAvailableHours-scoutingHours),telemetry);
            scoutingHours += FarmCrops(city, Math.Max(0,life.LaborAvailableHours-scoutingHours),telemetry);
            scoutingHours += ProcessCropFood(city, Math.Max(0,life.LaborAvailableHours-scoutingHours),telemetry);
            scoutingHours += TendPrimitiveHerd(city, Math.Max(0, life.LaborAvailableHours - scoutingHours), telemetry);
            scoutingHours += RunPrimitiveProcesses(city, Math.Max(0, life.LaborAvailableHours - scoutingHours), telemetry);
            life.LaborUsedHours = scoutingHours;
            var homeLaborShare = life.LaborAvailableHours > 0 ? Math.Max(0, 1 - scoutingHours / life.LaborAvailableHours) : 0;
            var homes = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house" && b.Status == "active" && b.Residents > 0).ToList();
            if (life.Unhoused > 0) homes.Add(new DwellingState
            {
                Id = $"camp:{city.Id}",
                CityId = city.Id,
                Kind = "camp",
                Residents = life.Unhoused,
                Cell = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!]
            });
            // Rotate the deterministic service order: first households must not always win scarce water/resources.
            if (homes.Count > 0) homes = homes.Skip(world.Day % homes.Count).Concat(homes.Take(world.Day % homes.Count)).ToList();
            var waterDeficit = Math.Max(0, Target(city, "water") - city.Stocks["water"]);
            foreach (var home in homes)
            {
                var budget = home.Residents * city.WorkerShare * multiplier * Rules.WorkHoursPerDay * homeLaborShare;
                var available = budget;
                // Each group supplies its own share: a river-side house must not
                // teleport an entire village's water into a shared inventory.
                available -= CollectWater(city, home, available, waterDeficit * home.Residents / Math.Max(1, population), telemetry);
                var activities = HouseholdActivities(city).Where(a => a.Discovery is null || life.Discoveries.Contains(a.Discovery))
                    .OrderByDescending(a => Urgency(city, a.Output) * ExpectedRate(city, home.Cell, a) * FoodPreference(city, home.Id, a)).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
                foreach (var activity in activities)
                {
                    var activityHours = 0d;
                    do
                    {
                        if (available <= 1e-6) break;
                        var target = Target(city, activity.Output);
                        var availableStock = city.Stocks[activity.Output] + (activity.Output == "food" ? EdibleFoodEquivalent(city) : 0);
                        var missing = Math.Max(0, target - availableStock);
                        missing = FoodActivityDeficit(city, activity, missing);
                        if (Rules.Primitive is not null && activity.Id == "hunt") missing = Math.Max(missing, Math.Max(0, PrimitiveTarget(city, "hides") - city.Stocks["hides"]) / .12);
                        if (missing <= 1e-8) break;
                        var destination = ActivitySite(city, home.Cell, activity);
                        if (destination is null) break;
                        var route = Routes(home.Cell); var t = terrain[destination.Value];
                        var travel = route.Cost.GetValueOrDefault(destination.Value) * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
                        // One daily work excursion per activity batch, cost proportional to participating workers.
                        var productiveFraction = Math.Max(0, 1 - travel / Rules.WorkHoursPerDay);
                        var rate = activity.OutputPerHour * productiveFraction * PrimitiveActivityFactor(city, activity, destination.Value);
                        if (activity.Pool is { } wildPool) rate *= EncounterRate(t, wildPool);
                        if (activity.Id == "garden") rate *= t.NaturalState.SoilQuality * Math.Max(0, 1 - layer.Construction.GetOccupiedCapacity(home.Cell) / 4d);
                        if (activity.Id == "cultivate") rate *= GardenSoil(destination.Value);
                        if (rate <= 1e-9) break;
                        var hours = Math.Min(available, budget * activity.MaximumLaborShare - activityHours);
                        var output = Math.Min(missing, hours * rate);
                        if (activity.Pool is { } p) output = Math.Min(output, Stock(t, p));
                        if (activity.Id == "cultivate") output = Math.Min(output, GardenRemaining(destination.Value));
                        foreach (var input in activity.Inputs) output = Math.Min(output, city.Stocks[input.Key] / input.Value);
                        hours = output / rate;
                        if (hours <= 1e-8) break;
                        if (activity.Pool is { } naturalPool) output = Extract(t, naturalPool, output, home.Cell);
                        if (activity.Id == "cultivate")
                        {
                            gardenTaken[destination.Value] = gardenTaken.GetValueOrDefault(destination.Value) + output;
                            RecordSoilHarvest(destination.Value, output);
                        }
                        foreach (var input in activity.Inputs)
                        {
                            city.Stocks[input.Key] = Math.Max(0, city.Stocks[input.Key] - output * input.Value);
                            Add(telemetry.IndustrialConsumptionByResource, input.Key, output * input.Value);
                        }
                        if (Rules.Primitive is not null && activity.Id == "hunt")
                        {
                            var hides = output * .12; output -= hides;
                            city.Stocks["hides"] += hides; Add(life.Production, "hides", hides); Add(telemetry.ProductionByResource, "hides", hides);
                        }
                        city.Stocks[activity.Output] += output; Add(life.Production, activity.Output, output); Add(telemetry.ProductionByResource, activity.Output, output);
                        if (activity.Output == "food") RecordFoodProduction(city, activity.Id, output);
                        Add(life.PracticeHours, activity.Id, hours * productiveFraction);
                        life.Tasks.Add(new(home.Id, activity.Id, destination.Value, hours, output));
                        RecordFoodTask(life, activity, hours, output, travel, route.Cost.GetValueOrDefault(destination.Value));
                        Passage(route.Path(destination.Value), hours / Rules.WorkHoursPerDay * 2);
                        available -= hours;
                        activityHours += hours;
                    } while (activity.Id == "cultivate" && available > 1e-6 && activityHours < budget * activity.MaximumLaborShare - 1e-6);
                }
                life.LaborUsedHours += budget - available;
            }
            PreserveWinterFood(city, telemetry);
            Discover(city);
            MoveHouseholds(city);
            MaintainBuildings(city, telemetry);
            EvaluateFields(city);
            Develop(city, telemetry);
            UpdateFoodSummary(city);
            UpdateMaintenanceSummary(city);
            var needed = population * content.Resources.Resources.Single(r => r.Id == "water").HouseholdNeed!.PerPersonPerDay;
            life.WaterCoverage = needed > 0 ? Math.Min(1, (RemoteConsumption(city.Id, "water") + Math.Min(city.Stocks["water"], Math.Max(0, needed - RemoteNeed(city.Id, "water")))) / needed) : 1;
            if (life.WaterCoverage < .99) city.Demography.Health = Math.Max(.1, city.Demography.Health - (1 - life.WaterCoverage) * .008);
            city.Infrastructure.Sanitation = Math.Clamp(.3 + life.WaterCoverage * .4, 0, 1);
            city.Infrastructure.HousingCondition = population > 0 ? Math.Clamp((Rules.Lifecycle is null ? life.HousingCapacity :
                State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house" && b.Status == "active").Sum(b => b.Residents * Efficiency(b))) / population, 0, 1) : 1;
            if (Rules.Decisions is { } decisionRules && life.Council is { } council) ObserveCouncilResults(city, council, decisionRules);
            EvaluateSupply(city);
        }
        State.Trails = edges.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
    }

    private double Target(CityState city, string resource)
    {
        var population = Population(city);
        var definition = content.Resources.Resources.Single(r => r.Id == resource);
        if (definition.HouseholdNeed is not null) return NeedsAndDemand.DailyHouseholdNeed(world, city, definition) * (resource == "water" ? 1.5 : Rules.ReserveDays) + PrimitiveTarget(city, resource);
        return Math.Max(PrimitiveTarget(city, resource), Math.Max(MaintenanceTarget(city, resource), resource switch { "timber" => Math.Max(.5, population * .004), "fiber" => population * .001, _ => 0 }));
    }
    private double Urgency(CityState city, string resource)
    {
        var target = Target(city, resource);
        if (target <= 0) return 0;
        return Math.Max(0, 1 - city.Stocks[resource] / target) * (resource == "food" ? 100 : resource == "firewood" ? 50 : 10);
    }

    private double CollectWater(CityState city, DwellingState home, double budget, double requested, DailyTelemetry telemetry)
    {
        if (Rules.Lifecycle is not null) return CollectStoredWater(city, home, budget, requested, telemetry);
        var life = State.Cities[city.Id]; var route = Routes(home.Cell);
        var wells = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "well" && b.Status == "active").Select(b => b.Cell).ToHashSet();
        var candidates = route.Cost.Keys.Where(c => terrain[c].Terrain != "water" && (terrain[c].Water.DistanceToRiver == 0 || wells.Contains(c)))
            .Where(c => waterTaken.GetValueOrDefault(c) < 2)
            .OrderBy(c => route.Cost[c]).ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal);
        var source = candidates.Cast<CellAddress?>().FirstOrDefault();
        if (source is null) return 0;
        var distance = route.Cost[source.Value] * world.Spatial.Grid.ZoneSizeMeters;
        var hoursPerTrip = distance * 2 / Rules.WalkingMetersPerHour + .08;
        var missing = Math.Min(requested, Math.Max(0, Target(city, "water") - city.Stocks["water"]));
        var collected = Math.Min(missing, Math.Min(2 - waterTaken.GetValueOrDefault(source.Value), budget * .65 / hoursPerTrip * Rules.CarryWaterTonnes));
        var trips = collected / Rules.CarryWaterTonnes;
        var hours = trips * hoursPerTrip;
        waterTaken[source.Value] = waterTaken.GetValueOrDefault(source.Value) + collected;
        city.Stocks["water"] += collected; life.WaterCollected += collected; life.WaterTravelHours += trips * distance * 2 / Rules.WalkingMetersPerHour;
        Add(life.Production, "water", collected); Add(telemetry.ProductionByResource, "water", collected); Add(life.PracticeHours, "water", hours);
        if (hours > 0) { life.Tasks.Add(new(home.Id, "water", source.Value, hours, collected)); Passage(route.Path(source.Value), trips * 2); }
        return hours;
    }

    private void Discover(CityState city)
    {
        if (Rules.Primitive is not null) { DiscoverPrimitive(city); return; }
        var life = State.Cities[city.Id];
        foreach (var discovery in Rules.Discoveries)
            if (!life.Discoveries.Contains(discovery.Id) && life.PracticeHours.GetValueOrDefault(discovery.Practice) >= discovery.PracticeHours)
            {
                life.Discoveries.Add(discovery.Id);
                Journal.Record(world, "household_discovery", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["discovery"] = discovery.Id, ["name"] = discovery.Name });
            }
        if (Rules.Lifecycle is { } lifecycle && !life.Discoveries.Contains("masonry") && life.PracticeHours.GetValueOrDefault("construction") >= lifecycle.MasonryPracticeHours)
        {
            life.Discoveries.Add("masonry");
            Journal.Record(world, "household_discovery", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["discovery"] = "masonry", ["name"] = "Каменная кладка" });
        }
    }

    private void Develop(CityState city, DailyTelemetry telemetry)
    {
        var life = State.Cities[city.Id];
        var homes = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house" && b.Status == "active").ToArray();
        var remainingStorageUse = life.Storage?.UsedByBuildingKind.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var building in State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active"))
        {
            var storageUsed = (building.Kind is "warehouse" or "granary") && remainingStorageUse.GetValueOrDefault(building.Kind) > 1e-9;
            if (storageUsed) remainingStorageUse[building.Kind] = Math.Max(0, remainingStorageUse[building.Kind] - BuildingRule(building.Kind).StorageCapacity * Efficiency(building));
            var used = building.Kind == "house" ? building.Residents > 0 || Moving(building) || life.HousingCapacity - Rules.ResidentsPerHouse < Population(city) * 1.05 :
                building.Kind == "garden" ? world.Day < building.ReadyDay || FieldDormant(building.Cell) || life.Tasks.Any(task => task.Activity == "cultivate" && task.Destination == building.Cell) :
                (building.Kind is "warehouse" or "granary") ? storageUsed :
                life.Tasks.Any(task => task.Activity == "water" && task.Destination == building.Cell);
            building.UnusedDays = used ? 0 : building.UnusedDays + 1;
        }
        var project = State.Buildings.FirstOrDefault(b => b.CityId == city.Id && b.Status == "building");
        if (Rules.Decisions is not null) project = CouncilConstruction(city, homes, project, telemetry);
        else if (project is null && homes.Length > 0)
        {
            var anchor = homes[0].Cell;
            var spare = life.HousingCapacity - Population(city);
            var desired = (int)Math.Ceiling(Population(city) * .05);
            if (spare < desired)
            {
                var site = FindBuildingSite(city, anchor);
                if (site is not null) project = StartProject(city, "house", site.Value, telemetry, "Нехватка жилья или резерв для роста");
            }
            if (project is null && life.Discoveries.Contains("well") && life.WaterTravelHours > Population(city) * .035 &&
                !State.Buildings.Any(b => b.CityId == city.Id && b.Kind == "well" && Standing(b)))
            {
                var site = FindBuildingSite(city, anchor, requireGroundwater: true);
                if (site is not null) project = StartProject(city, "well", site.Value, telemetry, "Сократить труд на доставку воды");
            }
            if (project is null && world.Day - life.LastRelocationDay >= Rules.RelocationCooldownDays && world.Day >= 30)
            {
                var worst = homes.Where(h => h.Residents > 0).OrderByDescending(h => WaterDistance(h.Cell, city.Id)).FirstOrDefault();
                if (worst is not null)
                {
                    var current = WaterDistance(worst.Cell, city.Id);
                    var route = Routes(worst.Cell);
                    var better = route.Cost.Keys.Where(c => terrain[c].AssignedCityId == city.Id && Free(c) && WaterDistance(c, city.Id, quick: true) == 0)
                        .OrderBy(c => route.Cost[c]).ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Cast<CellAddress?>().FirstOrDefault();
                    // Hysteresis: at least 800 m saved per round-trip, preparation and cooldown.
                    if (current > 4 && better is not null)
                        project = StartProject(city, "house", better.Value, telemetry, "Переезд ближе к воде", worst.Id);
                }
            }
        }
        if (project is null) return;
        var rule = BuildingRule(project.Kind);
        var origin = homes.FirstOrDefault()?.Cell ?? project.Cell;
        var projectRoute = Routes(origin);
        var productiveFraction = projectRoute.Cost.TryGetValue(project.Cell, out var distance) ? Math.Max(0, 1 - distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay) : 0;
        var hours = productiveFraction > 0 ? Math.Min(Math.Max(0, life.LaborAvailableHours - life.LaborUsedHours), Math.Min(life.LaborAvailableHours * .15, ((project.RequiredLaborHours ?? rule.LaborHours) - project.LaborDone) / productiveFraction)) : 0;
        if (project.ClearingRemaining is > 0 && Rules.Subsistence is { } subsistence)
        {
            var clearing = Math.Min(project.ClearingRemaining.Value, hours * productiveFraction * subsistence.ClearingTonnesPerHour);
            Extract(terrain[project.Cell], "timber", clearing);
            project.ClearingRemaining -= clearing;
        }
        project.LaborDone += hours * productiveFraction; life.LaborUsedHours += hours;
        if (Rules.Lifecycle is not null) Add(life.PracticeHours, "construction", hours * productiveFraction);
        project.UnusedDays = hours > 0 ? 0 : project.UnusedDays + 1;
        if (project.UnusedDays >= Rules.StalledConstructionAbandonAfterDays) { Abandon(project, "Стройка остановлена из-за нехватки труда или доступа"); return; }
        if (hours > 0)
        {
            Passage(projectRoute.Path(project.Cell), hours / Rules.WorkHoursPerDay * 2);
        }
        if (project.LaborDone + 1e-8 < (project.RequiredLaborHours ?? rule.LaborHours)) return;
        project.Status = "active";
        CompleteLifecycle(project);
        if (project.Kind == "garden") project.ReadyDay = world.Day + (BiologyRules is null ? Rules.Subsistence!.GardenGrowingDays : 0);
        if (project.Kind == "house" && project.Replaces is { } oldId)
        {
            project.MoveFinished = false;
            life.LastRelocationDay = world.Day;
            Journal.Record(world, "household_move_prepared", city.Id, [project.CauseEventId], new JsonObject { ["cityId"] = city.Id, ["from"] = oldId, ["to"] = project.Id });
        }
        Journal.Record(world, "settlement_building_completed", project.Id, [project.CauseEventId], new JsonObject { ["cityId"] = city.Id, ["kind"] = project.Kind });
    }

    private DwellingState? StartProject(CityState city, string kind, CellAddress cell, DailyTelemetry telemetry, string reason, string? replaces = null, string? decisionCause = null)
    {
        var rule = ProjectRule(city, kind);
        if (rule.Materials.Any(p => city.Stocks[p.Key] + 1e-9 < p.Value)) { State.Cities[city.Id].Decision = $"{reason}: не хватает материалов"; return null; }
        var building = AddBuilding(city, kind, cell, false, replaces);
        if (building.Lifecycle is { } lifecycle) lifecycle.InvestedMaterials = rule.Materials.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var material in rule.Materials) { city.Stocks[material.Key] -= material.Value; Add(telemetry.InfrastructureConsumptionByResource, material.Key, material.Value); }
        building.CauseEventId = Journal.Record(world, "settlement_building_started", building.Id, [decisionCause], new JsonObject { ["cityId"] = city.Id, ["kind"] = kind, ["reason"] = reason }).Id;
        State.Cities[city.Id].Decision = reason;
        return building;
    }

    private DwellingState AddBuilding(CityState city, string kind, CellAddress cell, bool initial, string? replaces = null)
    {
        var building = new DwellingState
        {
            Id = $"dwelling-{State.NextBuildingId++:000000}",
            CityId = city.Id,
            Kind = kind,
            Cell = cell,
            Slot = Enumerable.Range(0, layer.Construction.GetCapacity(cell)).First(slot => !State.Buildings.Any(b => b.Cell == cell && b.Slot == slot && b.Status != "demolished" && !(b.Kind == "garden" && b.Status == "abandoned"))),
            Status = initial ? "active" : "building",
            Replaces = replaces,
            LaborDone = initial ? BuildingRule(kind).LaborHours : 0
        };
        building.HouseholdId = building.Id;
        InitializeLifecycle(building, baseline: false, !initial && Rules.Lifecycle is not null && kind != "garden" ? ChooseMaterial(city, kind) : null);
        if (!initial && Rules.Lifecycle is not null) building.RequiredLaborHours = ProjectRule(city, kind).LaborHours;
        if (!initial && Rules.Subsistence is { } subsistence)
        {
            building.ClearingRemaining = Math.Min(Stock(terrain[cell], "timber"), kind == "garden" ? double.MaxValue : Capacity(terrain[cell], "timber") / layer.Construction.GetCapacity(cell));
            building.RequiredLaborHours = ProjectRule(city, kind).LaborHours + building.ClearingRemaining.Value / subsistence.ClearingTonnesPerHour;
        }
        PlaceInRegistry(building); State.Buildings.Add(building); return building;
    }
    private void PlaceInRegistry(DwellingState building)
    {
        if (building.Status == "demolished" || building.Kind == "garden" && building.Status == "abandoned") return; // Fallow land is not a permanent masonry ruin.
        if (!layer.Construction.Buildings.ContainsKey(building.Id)) layer.Construction.Place(new BuildingPlacement(building.Id, building.CityId,
            building.Status is "abandoned" or "demolishing" ? "ruin" : building.Kind, [new(building.Cell, building.Kind == "garden" ? layer.Construction.GetCapacity(building.Cell) : 1)], building.Status is "abandoned" or "demolishing" ? 0 : .55f));
    }
    private void Abandon(DwellingState building, string reason)
    {
        if (Rules.Wellbeing is not null && building.Residents > 0)
        {
            var campMembers = WellbeingHomes(world.Cities[building.CityId]).Where(h => h.Kind == "camp").Sum(h => h.Residents);
            TransferWellbeing(building.CityId, HouseholdIdentity(building.Id), $"camp:{building.CityId}", building.Residents, campMembers);
        }
        building.Status = "abandoned"; building.Residents = 0; layer.Construction.Remove(building.Id);
        if (building.Field is { } field) field.FallowSinceDay = world.Day;
        PlaceInRegistry(building); // Ruins lose influence, not their physical footprint.
        Journal.Record(world, "settlement_building_abandoned", building.Id, building.CauseEventId is null ? [] : [building.CauseEventId],
            new JsonObject { ["cityId"] = building.CityId, ["reason"] = reason });
    }
    private bool Free(CellAddress cell) => terrain[cell].Terrain != "water" &&
        !State.Cities.Values.Any(c=>c.Biology?.Herds.Values.Any(h=>h.Pasture==cell&&h.Count>0)==true) &&
        !layer.UsedLands.Any(l => l.Cell == cell && l.Usage > 0) && layer.Construction.GetOccupiedCapacity(cell) < layer.Construction.GetCapacity(cell);
    private CellAddress? FindBuildingSite(CityState city, CellAddress anchor, bool requireGroundwater = false) => Routes(anchor).Cost.Keys
        .Where(c => terrain[c].AssignedCityId == city.Id && Free(c) && (!requireGroundwater || terrain[c].Moisture >= .42 && terrain[c].ElevationMeters < 450))
        .OrderBy(c => Routes(anchor).Cost[c] + terrain[c].ForestCover * .1)
        .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Cast<CellAddress?>().FirstOrDefault();

    private CellAddress? BestResourceSite(CellAddress origin, string pool)
    {
        var route = Routes(origin);
        CellAddress? best = null; var bestScore = double.NegativeInfinity;
        foreach (var (cell, distance) in route.Cost)
        {
            var t = terrain[cell]; var stock = Stock(t, pool);
            // A winter shortcut does not automatically invent ice fishing or
            // harvesting resources through a closed ice cover.
            if (t.Terrain == "water") continue;
            if (stock <= 1e-8 || pool != "fish" && layer.Construction.GetOccupiedCapacity(cell) != 0) continue;
            var score = Rules.Subsistence is not null ? EncounterRate(t, pool) * Math.Max(0, 1 - distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour / Rules.WorkHoursPerDay) :
                stock / Math.Max(.001, Capacity(t, pool)) / (1 + distance * .12);
            if (score > bestScore || score == bestScore && (best is null || string.CompareOrdinal(t.Id, terrain[best.Value].Id) < 0))
            { best = cell; bestScore = score; }
        }
        return best;
    }
    private double WaterDistance(CellAddress cell, string cityId, bool quick = false)
    {
        if (terrain[cell].Water.DistanceToRiver == 0 || State.Buildings.Any(b => b.CityId == cityId && b.Kind == "well" && b.Status == "active" && b.Cell == cell)) return 0;
        if (quick) return double.PositiveInfinity;
        return Routes(cell).Cost.Where(p => terrain[p.Key].Terrain != "water" && (terrain[p.Key].Water.DistanceToRiver == 0 || State.Buildings.Any(b => b.CityId == cityId && b.Kind == "well" && b.Status == "active" && b.Cell == p.Key)))
            .Select(p => p.Value).DefaultIfEmpty(double.PositiveInfinity).Min();
    }

    private RouteTree Routes(CellAddress origin)
    {
        if (routes.TryGetValue(origin, out var cached)) return cached;
        var tree = new RouteTree(origin); var queue = new PriorityQueue<CellAddress, (double, long)>(); long sequence = 0;
        queue.Enqueue(origin, (0, sequence++));
        while (queue.TryDequeue(out var cell, out var priority))
        {
            if (priority.Item1 > tree.Cost[cell]) continue;
            foreach (var next in topology.GetNeighbors(cell))
            {
                if (!terrain.TryGetValue(next, out var t) || t.Terrain == "water" && !IcePassable(next)) continue;
                var ice = t.Terrain == "water" || terrain[cell].Terrain == "water";
                var cost = tree.Cost[cell] + (ice ? 1.2 : 1 + t.ForestCover * .6 + Math.Abs(t.ElevationMeters - terrain[cell].ElevationMeters) / 100) *
                    (ice ? 1 : 1 - trailStrength.GetValueOrDefault((cell, next)) * trailRules.MaximumCostReduction) * WeatherWalking(next);
                if (cost > 60 || tree.Cost.TryGetValue(next, out var old) && old <= cost) continue;
                tree.Cost[next] = cost; tree.Previous[next] = cell; queue.Enqueue(next, (cost, sequence++));
            }
        }
        routes[origin] = tree; return tree;
    }
    private void Passage(IReadOnlyList<CellAddress> path, double travelers)
    {
        if (travelers <= 0) return;
        for (var i = 1; i < path.Count; i++)
        {
            if (terrain.TryGetValue(path[i - 1], out var a) && a.Terrain == "water" || terrain.TryGetValue(path[i], out var b) && b.Terrain == "water") continue;
            var key = EdgeKey(path[i - 1], path[i]);
            if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new LocalTrailState { From = path[i - 1], To = path[i] };
            edge.Strength += (1 - edge.Strength) * (1 - Math.Exp(-travelers / trailRules.TrafficForStrongTrail)); edge.Passages += travelers;
        }
    }
    private void RebuildTrailStrength()
    {
        trailStrength.Clear();
        foreach (var edge in edges.Values)
        { trailStrength[(edge.From, edge.To)] = edge.Strength; trailStrength[(edge.To, edge.From)] = edge.Strength; }
    }
    private static string EdgeKey(CellAddress a, CellAddress b)
    {
        var x = SphericalSimulation.ZoneId(a); var y = SphericalSimulation.ZoneId(b);
        return string.CompareOrdinal(x, y) <= 0 ? $"{x}|{y}" : $"{y}|{x}";
    }
    private int Population(CityState city) => world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
    private static void Add(Dictionary<string, double> values, string key, double value) => values[key] = values.GetValueOrDefault(key) + value;
    private sealed class RouteTree(CellAddress origin)
    {
        public Dictionary<CellAddress, double> Cost { get; } = new() { [origin] = 0 };
        public Dictionary<CellAddress, CellAddress> Previous { get; } = new();
        public IReadOnlyList<CellAddress> Path(CellAddress end)
        {
            var path = new List<CellAddress> { end };
            while (Previous.TryGetValue(end, out var previous)) { end = previous; path.Add(end); }
            path.Reverse(); return path;
        }
    }
}
