using System.Text.Json.Nodes;
using WorldGen.Core.Content;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed class PrimitiveSettlementState
{
    public LocalWeather? Weather { get; set; }
    public double CalendarReserveDays { get; set; }
    public double StoredFoodTarget { get; set; }
    public double PreservedToday { get; set; }
    public double ReleasedToday { get; set; }
    public FoodComposition StoredComposition { get; set; } = new();
    public double HerdBiomass { get; set; }
    public double HerdCareHours { get; set; }
    public double HerdFeedToday { get; set; }
    public string? Representative { get; set; }
}

public sealed partial class SettlementSimulation
{
    private readonly SphericalTerrainGenerator? planetTerrain;
    private readonly Dictionary<CellAddress, LocalWeather> dailyWeather = new();
    private CellAddress Anchor(CityState city) => addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
    private bool Knows(CityState city, string id) => State.Cities[city.Id].Discoveries.Contains(id);

    private void InitializePrimitiveWorld()
    {
        if (Rules.Primitive is not { } r) return;
        if (planetTerrain is null) throw new InvalidOperationException("Начальному миру нужен генератор планеты");
        State.Atmosphere ??= SphericalWeather.Create(r, world.Spatial.Grid.Seed);
        foreach (var (cell, t) in terrain)
            State.Atmosphere.Ground.TryAdd(t.Id, new GroundWeatherState { SoilWater = t.Moisture });
        foreach (var city in world.Cities.Values)
            if (State.Cities[city.Id].Primitive is null) throw new InvalidOperationException("Нет состояния начальной эпохи в снимке");
        // Rebuild derived samples, but do not advance weather, reseed animals or rewrite technology on restore.
        BuildWeatherSamples();
        InitializeWeatherSurface();
    }
    private void BuildWeatherSamples(bool groundOnly = false)
    {
        if (!groundOnly) dailyWeather.Clear();
        if (Rules.Primitive is not { } r || State.Atmosphere is not { } sky) return;
        foreach (var (cell, t) in terrain)
            dailyWeather[cell] = groundOnly && dailyWeather.TryGetValue(cell, out var previous)
                ? previous with { SoilWater = sky.Ground[t.Id].SoilWater, Snow = sky.Ground[t.Id].Snow, Fire = sky.Ground[t.Id].Fire }
                : SphericalWeather.Sample(sky, r, topology.ToUnitVector(cell), t.TemperatureC,
                    Math.Max(0, sky.LastDay), world.Calendar.DaysPerYear, sky.Ground[t.Id]);
    }
    private void AdvancePrimitiveWeather()
    {
        if (Rules.Primitive is not { } r || State.Atmosphere is not { } sky) return;
        SphericalWeather.Advance(sky, r, world.Spatial.Grid.Seed, world.Day,
            p => planetTerrain!.SampleSurface(p).Biome == SphericalBiome.Ocean);
        BuildWeatherSamples();
        foreach (var (cell, t) in terrain)
        {
            var ground = sky.Ground[t.Id];
            SphericalWeather.WetGround(ground, dailyWeather[cell]);
            if (r.Winter is { } winter && t.Terrain == "water")
                ground.IceMeters = WinterWeather.IceAfterDay(ground.IceMeters, dailyWeather[cell], winter, t.Water.River);
        }
        AdvanceWeatherSurface();
        BuildWeatherSamples(groundOnly: true);
        var fires = new Dictionary<CellAddress, double>();
        var spreading = new Dictionary<CellAddress, double>();
        var index = 0;
        foreach (var (cell, t) in terrain.OrderBy(p => p.Value.Id, StringComparer.Ordinal))
        {
            var weather = dailyWeather[cell]; var ground = sky.Ground[t.Id];
            var fuel = Stock(t, "timber"); var random = SphericalWeather.Random(world.Spatial.Grid.Seed, world.Day, index++);
            var fire = ground.Fire;
            if (fire == 0 && t.Terrain != "water" && fuel > 2 && ground.SoilWater < .3 && weather.Storm > .25 &&
                random < r.LightningIgnitionChance * weather.Storm)
            {
                fire = .3; sky.Ignitions++;
                Journal.Record(world, "lightning_fire", t.Id, details: new JsonObject { ["reason"] = "Молния зажгла сухой лес", ["cityId"] = t.AssignedCityId });
            }
            if (fire <= 0) continue;
            fire = Math.Clamp(fire + .12 * (1 - ground.SoilWater) - weather.RainMm * .09 - ground.Snow * .02, 0, 1);
            var burned = Math.Min(fuel, fire * (.08 + weather.Wind * .1) * fuel);
            SetStock(t, "timber", fuel - burned); ground.BurnedTimber += burned; sky.BurnedTimber += burned;
            fires[cell] = fuel - burned > .1 ? fire : 0;
            foreach (var building in State.Buildings.Where(b => b.Cell == cell && b.Lifecycle is not null && b.Status == "active"))
                building.Lifecycle!.RepairableWear = Math.Min(1 - building.Lifecycle.PermanentWear, building.Lifecycle.RepairableWear + fire * .015);
            // Synchronous next-day front: a day's fire cannot recurse across the entire connected forest.
            if (fire < .2) continue;
            foreach (var neighbor in topology.GetNeighbors(cell))
                if (terrain.TryGetValue(neighbor, out var next) && next.Terrain != "water" && Stock(next, "timber") > 2 &&
                    sky.Ground[next.Id].SoilWater < .4 && dailyWeather[neighbor].RainMm < 3)
                {
                    var p = topology.ToUnitVector(cell); var q = topology.ToUnitVector(neighbor);
                    var tangentLength = Math.Max(1e-9, Math.Sqrt(p.X * p.X + p.Z * p.Z));
                    var stepLength = Math.Max(1e-9, Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2) + Math.Pow(q.Z - p.Z, 2)));
                    var downwind = (-p.Z * (q.X - p.X) + p.X * (q.Z - p.Z)) / tangentLength / stepLength;
                    var spread = fire * (.45 + .25 * weather.Wind * downwind) * Math.Clamp(Stock(next, "timber") / 20, .05, 1);
                    spreading[neighbor] = Math.Max(spreading.GetValueOrDefault(neighbor), spread);
                }
        }
        foreach (var (cell, intensity) in spreading) fires[cell] = Math.Max(fires.GetValueOrDefault(cell), intensity);
        foreach (var (cell, t) in terrain) sky.Ground[t.Id].Fire = fires.GetValueOrDefault(cell);
        BuildWeatherSamples(groundOnly: true);
        foreach (var city in world.Cities.Values)
        {
            var p = State.Cities[city.Id].Primitive!; var anchor = Anchor(city);
            p.Weather = dailyWeather[anchor];
            p.CalendarReserveDays = Knows(city, "calendar") ? SphericalWeather.CalendarReserve(r, terrain[anchor].TemperatureC,
                topology.ToUnitVector(anchor), world.Day, world.Calendar.DaysPerYear) : 0;
            var decay = content.Resources.Resources.Single(x => x.Id == "winter_food").DecayPerDay;
            var days = Math.Min(Math.Max(3, p.CalendarReserveDays), decay > 0 ? .35 / decay : r.WinterReserveDays);
            p.StoredFoodTarget = Knows(city, "calendar") ? Math.Min(Population(city) * r.StorageFoodPerResident, Population(city) * city.FoodPerPersonPerDay * days) : 0;
            p.StoredComposition.Reconcile(city.Stocks["winter_food"]);
            // Cold exposure is harmful when actual clothing and shelter are insufficient.
            var exposure = Math.Max(0, -p.Weather.TemperatureC) / 20 *
                (1 - Math.Clamp(city.Stocks["garments"] / Math.Max(1, Population(city)), 0, 1) * .7) * (1 - city.Infrastructure.HousingCondition * .7);
            city.Demography.Health = Math.Max(.1, city.Demography.Health - exposure * .003);
        }
    }
    private double WeatherGrowth(CellAddress cell) => dailyWeather.TryGetValue(cell, out var w) ? SphericalWeather.Growth(w) : 1;
    private bool FieldDormant(CellAddress cell) => dailyWeather.TryGetValue(cell, out var w) && (w.TemperatureC < 5 || w.Snow > 10);
    private double WeatherWalking(CellAddress cell) => dailyWeather.TryGetValue(cell, out var w) ? WinterWeather.WalkingCost(w) : 1;
    public bool IcePassable(CellAddress cell) => Rules.Primitive?.Winter is { } r && terrain.TryGetValue(cell, out var t) &&
        t.Terrain == "water" && dailyWeather.TryGetValue(cell, out var w) && State.Atmosphere!.Ground.TryGetValue(t.Id, out var g) &&
        WinterWeather.Passable(g.IceMeters, w.TemperatureC, t.Biome == "ocean", t.Water.River, r);
    private double WeatherRecharge(CellAddress cell) => dailyWeather.TryGetValue(cell, out var w) ? .2 + w.SoilWater * 1.2 : 1;
    private double KitCoverage(CityState city) => Math.Clamp(city.Stocks.GetValueOrDefault("stone_kit") / Math.Max(1, Population(city) * .08), 0, 1);
    private double WaterCarry(CityState city) => Rules.CarryWaterTonnes * (Rules.Primitive is null ? 1 : .35 + KitCoverage(city) * .65);
    private double PrimitiveMaterialLabor(CityState city, string material) => Rules.Primitive is not null && material == "wood" && !Knows(city, "woodworking") ? 4 : 1;

    private double PrimitiveActivityFactor(CityState city, HouseholdActivityRule a, CellAddress cell)
    {
        if (Rules.Primitive is null) return 1;
        var kit = KitCoverage(city);
        var weather = dailyWeather.GetValueOrDefault(cell);
        var factor = a.Id switch
        {
            "wood" => (.25 + .75 * kit) * (Knows(city, "woodworking") ? 1.6 : .65),
            "hunt" => (.4 + .6 * kit) * (1 + Math.Clamp(city.Stocks["primitive_bow"] / Math.Max(1, Population(city) * .02), 0, 1) * .8),
            "gather" => .12 + .88 * WeatherGrowth(cell),
            "garden" or "cultivate" => WeatherGrowth(cell),
            "fish" => weather is null ? 1 : Math.Clamp(1 - weather.Wind * .5 - weather.Snow / 150, .2, 1),
            _ => 1d
        };
        return factor * (1 - (weather?.Fire ?? 0));
    }
    private double PrimitiveTarget(CityState city, string resource)
    {
        if (Rules.Primitive is null) return 0;
        var p = State.Cities[city.Id].Primitive!; var population = Population(city);
        var missingStored = Math.Max(0, p.StoredFoodTarget - city.Stocks["winter_food"]);
        return resource switch
        {
            "food" => Math.Min(missingStored, population * city.FoodPerPersonPerDay * 3),
            "firewood" => population * .00022 * p.CalendarReserveDays + missingStored * Rules.Primitive.PreserveFuelPerFood,
            "stone_kit" => population * .08,
            "primitive_bow" => Knows(city, "archery") ? population * .02 : 0,
            "garments" => population,
            "hides" => population * .0003,
            "cloth" => Knows(city, "weaving") ? population * .0005 : 0,
            "stone" => .04,
            _ => 0
        };
    }
    private void PreserveWinterFood(CityState city, DailyTelemetry telemetry)
    {
        if (Rules.Primitive is not { } r) return;
        var life = State.Cities[city.Id]; var p = life.Primitive!;
        p.PreservedToday = p.ReleasedToday = 0;
        var daily = Population(city) * city.FoodPerPersonPerDay;
        var available = Math.Max(0, life.LaborAvailableHours - life.LaborUsedHours);
        if (Knows(city, "fire") && Knows(city, "calendar"))
        {
            var amount = Math.Min(Math.Max(0, p.StoredFoodTarget - city.Stocks["winter_food"]),
                Math.Min(Math.Max(0, city.Stocks["food"] - daily * Rules.ReserveDays),
                Math.Min(Math.Min(available, life.LaborAvailableHours * .12) * r.PreserveFoodPerHour,
                    Math.Max(0, city.Stocks["firewood"] - daily * .1) / r.PreserveFuelPerFood)));
            city.Stocks["food"] -= amount; city.Stocks["winter_food"] += amount; city.Stocks["firewood"] -= amount * r.PreserveFuelPerFood;
            var composition = life.Wellbeing?.FoodStock;
            if (composition is not null) foreach (var (category, n) in composition.Take(amount)) p.StoredComposition.Add(category, n);
            p.StoredComposition.Reconcile(city.Stocks["winter_food"]);
            life.LaborUsedHours += amount / r.PreserveFoodPerHour; p.PreservedToday = amount;
            Add(telemetry.IndustrialConsumptionByResource, "food", amount); Add(telemetry.IndustrialConsumptionByResource, "firewood", amount * r.PreserveFuelPerFood);
            Add(telemetry.ProductionByResource, "winter_food", amount);
            if (amount > 0) life.Tasks.Add(new($"camp:{city.Id}", "preserve", Anchor(city), amount / r.PreserveFoodPerHour, amount));
        }
        var release = Math.Min(city.Stocks["winter_food"], Math.Max(0, daily * 1.5 - city.Stocks["food"]));
        city.Stocks["winter_food"] -= release; city.Stocks["food"] += release; p.ReleasedToday = release;
        foreach (var (category, n) in p.StoredComposition.Take(release)) life.Wellbeing?.FoodStock.Add(category, n);
        Add(telemetry.IndustrialConsumptionByResource, "winter_food", release); Add(telemetry.ProductionByResource, "food", release);
    }
    private double TendPrimitiveHerd(CityState city, double available, DailyTelemetry telemetry)
    {
        if (Rules.Primitive is null) return 0;
        if (BiologyRules is not null) return TendSpeciesHerds(city,available,telemetry);
        var life = State.Cities[city.Id]; var p = life.Primitive!;
        p.HerdCareHours = p.HerdFeedToday = 0;
        if (!Knows(city, "taming")) return 0;
        var anchor = Anchor(city); var feedSite = BestResourceSite(anchor, "forage");
        var budget = Math.Min(available, life.LaborAvailableHours * .04);
        var travel = feedSite is { } destination ? Routes(anchor).Cost[destination] * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour : 0;
        var care = p.HerdBiomass * (40 + travel * 10);
        var feed = p.HerdBiomass * .004;
        var water = p.HerdBiomass * .01;
        var coverage = Math.Min(1, Math.Min(budget / Math.Max(1e-9, care),
            Math.Min((feedSite is { } f ? Stock(terrain[f], "forage") : 0) / Math.Max(1e-9, feed), city.Stocks["water"] / Math.Max(1e-9, water))));
        if (feedSite is { } site) p.HerdFeedToday = Extract(terrain[site], "forage", feed * coverage);
        city.Stocks["water"] -= water * coverage;
        Add(telemetry.IndustrialConsumptionByResource, "water", water * coverage);
        p.HerdCareHours = care * coverage;
        // Herd growth cannot exceed the dry feed actually gathered. Unfed animals die.
        p.HerdBiomass += Math.Min(p.HerdFeedToday * .2, p.HerdBiomass * .0008) - p.HerdBiomass * (1 - coverage) * .01;
        var target = Population(city) * .002;
        if (p.HerdBiomass < target && budget > p.HerdCareHours && city.Stocks["food"] > Population(city) * city.FoodPerPersonPerDay * 2 && BestResourceSite(anchor, "game") is { } game)
        {
            var captured = Extract(terrain[game], "game", Math.Min(target - p.HerdBiomass, (budget - p.HerdCareHours) * .0005), anchor);
            p.HerdBiomass += captured; p.HerdCareHours += captured / .0005;
        }
        if (p.HerdBiomass > target * .5)
        {
            var slaughter = Math.Min(Math.Max(0, budget - p.HerdCareHours) * .002,
                Math.Min(p.HerdBiomass - target * .5, Math.Max(0, Population(city) * city.FoodPerPersonPerDay - city.Stocks["food"])));
            p.HerdCareHours += slaughter / .002;
            p.HerdBiomass -= slaughter;
            city.Stocks["food"] += slaughter * .88; city.Stocks["hides"] += slaughter * .12;
            RecordFoodProduction(city, "hunt", slaughter * .88);
            Add(telemetry.ProductionByResource, "food", slaughter * .88); Add(telemetry.ProductionByResource, "hides", slaughter * .12);
        }
        if (p.HerdCareHours > 0) life.Tasks.Add(new($"camp:{city.Id}", "herd", feedSite ?? anchor, p.HerdCareHours, p.HerdFeedToday));
        return p.HerdCareHours;
    }
    private void DiscoverPrimitive(CityState city)
    {
        var r = Rules.Primitive!; var life = State.Cities[city.Id];
        var knownBefore = life.Discoveries.ToHashSet(StringComparer.Ordinal);
        foreach (var tech in r.Technologies)
        {
            var knowledge = city.TechnologyState[tech.Id];
            if(BiologyRules is {} bio)
            {
                var state=life.Biology!;
                if(tech.Id=="crop_rotation"&&state.HarvestedCrops.Count<bio.RotationCropCount)continue;
                var crop=bio.Crops.FirstOrDefault(c=>c.Technology==tech.Id);
                var animal=bio.Animals.FirstOrDefault(a=>a.Technology==tech.Id);
                if(crop is not null&&!state.KnownPlants.Contains(crop.Id)||animal is not null&&!state.KnownAnimals.Contains(animal.Id))continue;
            }
            if (!knownBefore.Contains(tech.Id) && tech.Prerequisites.All(knownBefore.Contains))
            {
                knowledge.Knowledge = Math.Min(1, life.PracticeHours.GetValueOrDefault(tech.Practice) / tech.PracticeHours);
                if (knowledge.Knowledge >= 1)
                {
                    life.Discoveries.Add(tech.Id);
                    Journal.Record(world, "household_discovery", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["discovery"] = tech.Id, ["name"] = tech.Name });
                }
            }
            if (!life.Discoveries.Contains(tech.Id)) continue;
            knowledge.Competence = Math.Clamp(.5 + life.PracticeHours.GetValueOrDefault(tech.Practice) / tech.PracticeHours * .5, 0, 1);
            knowledge.Capability = tech.Id switch
            {
                "stone_axes" or "spears" or "stone_vessels" => KitCoverage(city),
                "archery" => Math.Clamp(city.Stocks["primitive_bow"] / Math.Max(1, Population(city) * .02), 0, 1),
                "hide_clothing" => Math.Clamp(city.Stocks["garments"] / Math.Max(1, Population(city)), 0, 1),
                "gardening" => Math.Clamp(State.Buildings.Count(b => b.CityId == city.Id && ReadyGarden(b)) / 4d, 0, 1),
                "well" => State.Buildings.Any(b => b.CityId == city.Id && b.Kind == "well" && b.Status == "active") ? 1 : 0,
                "taming" => Math.Clamp(life.Primitive!.HerdBiomass / Math.Max(.01, Population(city) * .002), 0, 1),
                _ => 1
            };
            knowledge.Adoption = knowledge.Capability;
            if(BiologyRules is {} biology)
            {
                var crop=biology.Crops.FirstOrDefault(c=>c.Technology==tech.Id);
                var animal=biology.Animals.FirstOrDefault(a=>a.Technology==tech.Id);
                if(crop is not null){knowledge.Capability=Math.Clamp(city.Stocks[crop.SeedResource]/crop.SeedTonnes,0,1);knowledge.Adoption=Math.Clamp(life.Biology!.Plots.Values.Where(p=>p.CropId==crop.Id).Sum(p=>p.Area)/3,0,1);}
                if(animal is not null)knowledge.Adoption=knowledge.Capability=Math.Clamp((life.Biology!.Herds.GetValueOrDefault(animal.Id)?.Count??0)/4d,0,1);
            }
        }
    }
}
