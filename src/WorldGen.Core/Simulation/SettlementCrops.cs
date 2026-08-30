using System.Text.Json.Nodes;
using WorldGen.Core.Topology;
namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private readonly Dictionary<CellAddress, CropRule[]> wildCrops = new();
    private BiosphereRules? BiologyRules => Rules.Primitive?.Biosphere;
    private void InitializeBiology()
    {
        if (BiologyRules is null) return;
        foreach (var life in State.Cities.Values) life.Biology ??= new();
        foreach (var city in world.Cities.Values) foreach (var (id, herd) in State.Cities[city.Id].Biology!.Herds)
            if (herd.Pasture is { } cell && herd.Count > 0 && herd.PastureWork >= 24) RegisterPasture(city.Id, id, cell);
    }
    private CropRule[] WildCrops(CellAddress cell)
    {
        if (wildCrops.TryGetValue(cell, out var cached)) return cached;
        var p = topology.ToUnitVector(cell);
        var local = terrain.GetValueOrDefault(cell);
        var surface = local is null ? planetTerrain?.SampleSurface(p) : null;
        var water = local?.Terrain == "water" || surface?.Biome == SphericalBiome.Ocean;
        var temperature = local?.TemperatureC ?? surface?.TemperatureC ?? 0;
        var moisture = local?.Moisture ?? surface?.Moisture ?? 0;
        var forest = local?.ForestCover ?? surface?.ForestCover ?? 0;
        var found = BiologyRules!.Crops.Where(c => !water && Biosphere.WildScore(c.Id, c.Habitat, world.Spatial.Grid.Seed, p, temperature, moisture, forest) > .22)
            .OrderByDescending(c => Biosphere.WildScore(c.Id, c.Habitat, world.Spatial.Grid.Seed, p, temperature, moisture, forest)).ToArray();
        wildCrops[cell] = found; return found;
    }
    private double CropSuitability(CropRule crop, CellAddress cell)
    {
        var t = terrain[cell]; return t.Terrain == "water" ? 0 : Biosphere.Suitability(crop.Habitat, t.TemperatureC, t.Moisture, Math.Clamp(t.ForestCover, crop.Habitat.MinForest, crop.Habitat.MaxForest));
    }
    private bool CanSow(CropRule crop, CellAddress cell)
    {
        var w = dailyWeather[cell]; if (w.Snow > 2 || w.TemperatureC < crop.BaseTemperature + 2 || !CanSupportCropCycle(crop, cell)) return false;
        var future = 0d; var point = topology.ToUnitVector(cell);
        // Calendar knowledge of the seasonal cycle, not access to future storms.
        for (var offset = 0; offset < 180; offset += 10)
            future += Math.Max(0, SphericalWeather.SeasonalTemperature(terrain[cell].TemperatureC, point, Rules.Primitive!.SeasonalAmplitudeC, world.Day + offset, world.Calendar.DaysPerYear) - crop.BaseTemperature) * 10;
        return future >= crop.DegreeDays || crop.MatureYears > 0;
    }
    private bool CanSupportCropCycle(CropRule crop, CellAddress cell)
    {
        if (CropSuitability(crop, cell) <= 0) return false;
        if (crop.MatureYears > 0) return true;
        var annual = 0d; var point = topology.ToUnitVector(cell); var step = 10;
        for (var offset = 0; offset < world.Calendar.DaysPerYear; offset += step)
            annual += Math.Max(0, SphericalWeather.SeasonalTemperature(terrain[cell].TemperatureC, point,
                Rules.Primitive!.SeasonalAmplitudeC, world.Day + offset, world.Calendar.DaysPerYear) - crop.BaseTemperature) *
                Math.Min(step, world.Calendar.DaysPerYear - offset);
        return annual >= crop.DegreeDays;
    }
    private IEnumerable<CropRule> CropChoices(CityState city, CellAddress cell)
    {
        var bio = State.Cities[city.Id].Biology!;
        var active = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "garden" && Standing(b))
            .Select(b => bio.Plots.GetValueOrDefault(b.Id)).Where(p => p?.CropId is not null).ToArray();
        var perennials = active.Count(p => BiologyRules!.Crops.Single(c => c.Id == p!.CropId).MatureYears > 0);
        return BiologyRules!.Crops.Where(c => Knows(city, c.Technology) && city.Stocks[c.SeedResource] >= c.SeedTonnes * .015 && CropSuitability(c, cell) > 0)
            // One trial per unfamiliar species. Food fields must not all become juvenile orchards.
            .Where(c => bio.HarvestedCrops.Contains(c.Id) || !active.Any(p => p!.CropId == c.Id))
            .Where(c => c.MatureYears == 0 || perennials < Math.Max(1, (active.Length + 1) * .25));
    }
    private CropRule[] PendingTechnicalCropTrials(CityState city)
    {
        if (BiologyRules is null) return [];
        var bio = State.Cities[city.Id].Biology!;
        var planted = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "garden" && Standing(b))
            .Select(b => bio.Plots.GetValueOrDefault(b.Id)?.CropId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        return BiologyRules.Crops.Where(c => c.FoodValue <= 0 && Knows(city, c.Technology) &&
                city.Stocks[c.SeedResource] >= c.SeedTonnes * .015 && !bio.HarvestedCrops.Contains(c.Id) && !planted.Contains(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }
    private bool CanStartCropPlot(CityState city, CellAddress cell) => BiologyRules is null || CropChoices(city, cell).Any();
    private double CropExpectedDailyYield(CellAddress cell)
    {
        var building = State.Buildings.FirstOrDefault(b => b.Cell == cell && b.Kind == "garden" && Standing(b));
        var state = building is null ? null : State.Cities[building.CityId].Biology?.Plots.GetValueOrDefault(building.Id);
        var crop = BiologyRules?.Crops.FirstOrDefault(c => c.Id == state?.CropId);
        return crop is null ? 0 : crop.YieldTonnes * crop.FoodValue * Math.Max(.1, state!.Area) * terrain[cell].NaturalState.SoilQuality / Math.Max(120, crop.DegreeDays / Math.Max(2, terrain[cell].TemperatureC - crop.BaseTemperature));
    }
    private double CropUtility(CityState city, CropRule crop, CellAddress cell)
    {
        var suitability = CropSuitability(crop, cell);
        if (crop.FoodValue > 0)
        {
            var pressure = Math.Clamp((Target(city, "food") - city.Stocks["food"]) / Math.Max(.001, Target(city, "food")), 0, 1);
            return crop.YieldTonnes * crop.FoodValue * suitability * (.5 + pressure);
        }
        var demanded = Rules.Primitive?.Processes.Any(process => Knows(city, process.Technology) && process.Inputs.ContainsKey(crop.HarvestResource) &&
            city.Stocks.GetValueOrDefault(process.TargetResource) < Population(city) * process.TargetOutputPerPerson) == true;
        return crop.YieldTonnes * suitability * (demanded ? 2 : .2);
    }
    private double SearchSeeds(CityState city, double available, DailyTelemetry telemetry)
    {
        if (BiologyRules is not { } r || available <= 0) return 0;
        var anchor = Anchor(city); var routesFromHome = Routes(anchor); var bio = State.Cities[city.Id].Biology!;
        // Only a reached and worked patch teaches species; no planet-wide search.
        var sites = routesFromHome.Cost.Where(p => p.Value < 16 && terrain[p.Key].Terrain != "water" && Stock(terrain[p.Key], "forage") > .001)
            .OrderBy(p => p.Value).ThenBy(p => SphericalSimulation.ZoneId(p.Key), StringComparer.Ordinal).Take(180).ToArray();
        var candidates = sites.SelectMany(p => WildCrops(p.Key).Select(c => (Cell: p.Key, Distance: p.Value, Crop: c)))
            .Where(p => p.Crop.Habitat.MinForest <= .05 || terrain[p.Cell].NaturalState.ForestBiomass > .05)
            .Where(p => city.Stocks[p.Crop.SeedResource] < p.Crop.SeedTonnes * 2)
            .OrderBy(p => bio.KnownPlants.Contains(p.Crop.Id) ? 1 : 0)
            .ThenBy(p => State.Cities[city.Id].PracticeHours.GetValueOrDefault("seed:" + p.Crop.Id))
            .ThenBy(p => city.Stocks[p.Crop.SeedResource] / p.Crop.SeedTonnes + p.Distance * .01).FirstOrDefault();
        if (candidates.Crop is null) return 0;
        var travel = candidates.Distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
        var hours = Math.Min(available, r.SearchHoursPerDay); if (hours <= travel) return 0;
        var amount = Extract(terrain[candidates.Cell], "forage", (hours - travel) * r.SeedTonnesPerSearchHour);
        city.Stocks[candidates.Crop.SeedResource] += amount; bio.SeedCollected += amount;
        Add(telemetry.ProductionByResource, candidates.Crop.SeedResource, amount);
        if (amount > 0 && bio.KnownPlants.Add(candidates.Crop.Id)) Journal.Record(world, "plant_discovered", city.Id, details: new JsonObject
        {
            ["cityId"] = city.Id,
            ["species"] = candidates.Crop.Id,
            ["name"] = candidates.Crop.Name,
            ["cell"] = SphericalSimulation.ZoneId(candidates.Cell),
            ["reason"] = "Собран дикий посадочный материал"
        });
        Add(State.Cities[city.Id].PracticeHours, "seed:" + candidates.Crop.Id, hours - travel);
        Add(State.Cities[city.Id].PracticeHours, "gather", hours - travel);
        State.Cities[city.Id].Tasks.Add(new("camp:" + city.Id, "seed_search", candidates.Cell, hours, amount)); Passage(routesFromHome.Path(candidates.Cell), 2);
        return hours;
    }
    private double FarmCrops(CityState city, double available, DailyTelemetry telemetry)
    {
        if (BiologyRules is not { } r) return 0;
        var life = State.Cities[city.Id]; var bio = life.Biology!; var origin = Anchor(city); var route = Routes(origin);
        var limit = Math.Min(available, life.LaborAvailableHours * r.FarmingLaborShare); var spent = 0d;
        var buildings = State.Buildings.Where(b => b.CityId == city.Id && ReadyGarden(b)).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        // Rotate allocation so a poor village does not always starve the same field.
        if (buildings.Length > 0) buildings = buildings.Skip(world.Day % buildings.Length).Concat(buildings.Take(world.Day % buildings.Length)).ToArray();
        foreach (var b in buildings)
        {
            if (!bio.Plots.TryGetValue(b.Id, out var plot)) bio.Plots[b.Id] = plot = new();
            if (plot.LastDay == world.Day) continue; plot.LastDay = world.Day;
            var distance = route.Cost.GetValueOrDefault(b.Cell, double.PositiveInfinity);
            var travel = distance * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
            var budget = Math.Max(0, limit - spent); var hours = 0d;
            if (plot.CropId is null && budget > travel)
            {
                var options = CropChoices(city, b.Cell).Where(c => CanSow(c, b.Cell))
                    .OrderBy(c => Knows(city, "crop_rotation") && c.Family == plot.LastFamily ? 1 : 0)
                    // A known technical crop receives one experimental plot before
                    // ordinary food expansion. Once planted it leaves this priority.
                    .ThenBy(c => c.FoodValue <= 0 && !bio.HarvestedCrops.Contains(c.Id) ? 0 : 1)
                    .ThenBy(c => bio.HarvestedCrops.Contains(c.Id) ? 1 : 0)
                    .ThenBy(c => c.MatureYears > 0 ? 1 : 0)
                    .ThenBy(c => buildings.Count(b => bio.Plots.GetValueOrDefault(b.Id)?.CropId == c.Id))
                    .ThenByDescending(c => CropUtility(city, c, b.Cell) / c.DegreeDays).ToArray();
                if (options.Length > 0)
                {
                    var selected = options[0]; var area = Math.Min(1, Math.Min(city.Stocks[selected.SeedResource] / selected.SeedTonnes, (budget - travel) / selected.PlantHours));
                    if (area >= .015)
                    {
                        var seeds = selected.SeedTonnes * area; city.Stocks[selected.SeedResource] -= seeds; Add(telemetry.IndustrialConsumptionByResource, selected.SeedResource, seeds);
                        plot.CropId = selected.Id; plot.Area = area; plot.Health = 1; plot.AgeDays = plot.DegreeDays = 0; plot.SeedSaved = false;
                        plot.Phase = area < .9 ? "размножение семян" : selected.MatureYears > 0 ? "молодые посадки" : "посев";
                        hours = selected.PlantHours * area + travel;
                        if (Knows(city, "crop_rotation") && plot.LastFamily is { } family && family != selected.Family && selected.Family == "legume")
                            terrain[b.Cell].NaturalState.SoilQuality = Math.Min(1, terrain[b.Cell].NaturalState.SoilQuality + .025 * area);
                        Journal.Record(world, "crop_sown", b.Id, details: new JsonObject { ["cityId"] = city.Id, ["crop"] = selected.Id, ["area"] = area, ["seedTonnes"] = seeds });
                    }
                }
                else plot.Phase = "ожидание семян или сезона";
            }
            if (plot.CropId is { } id)
            {
                var crop = r.Crops.Single(c => c.Id == id); var w = dailyWeather[b.Cell]; plot.AgeDays++;
                var growing = Biosphere.Growth(crop, w.TemperatureC, w.SoilWater, w.Snow);
                if (plot.HarvestRemaining <= 0)
                {
                    var care = crop.CareHours / Math.Max(60, crop.DegreeDays / 10) * plot.Area;
                    var visit = hours > 0 ? 0 : travel;
                    var tended = Math.Min(Math.Max(0, budget - hours - visit), growing > 0 ? care : 0);
                    if (tended > 0) hours += tended + visit;
                    if (growing > 0) plot.Health = Math.Clamp(plot.Health + (tended / Math.Max(.001, care) - .6) * .006, 0, 1);
                    if (w.TemperatureC < crop.FrostTolerance && crop.MatureYears == 0) plot.Health = Math.Max(0, plot.Health - .12);
                    if (crop.MatureYears == 0 && plot.AgeDays > world.Calendar.DaysPerYear) plot.Health = Math.Max(0, plot.Health - .12);
                    plot.DegreeDays += growing * (.35 + .65 * plot.Health);
                    if (plot.Health <= .05)
                    {
                        plot.CropId = null; plot.Area = 0; plot.Phase = "посев погиб"; plot.FailedSeasons++;
                        Journal.Record(world, "crop_failed", b.Id, details: new JsonObject { ["cityId"] = city.Id, ["crop"] = id, ["reason"] = "Мороз или недостаточный уход" });
                    }
                    else if (plot.AgeDays >= crop.MatureYears * world.Calendar.DaysPerYear && plot.DegreeDays >= crop.DegreeDays && growing > 0 &&
                        (crop.MatureYears == 0 || plot.LastHarvestDay < 0 || world.Day - plot.LastHarvestDay >= 250))
                    {
                        plot.HarvestRemaining = crop.YieldTonnes * plot.Area * plot.Health * terrain[b.Cell].NaturalState.SoilQuality * CropSuitability(crop, b.Cell);
                        plot.Phase = "созревший урожай"; plot.SeedSaved = false;
                    }
                    else if (growing <= 0) plot.Phase = "сезонный покой";
                }
                if (plot.CropId is not null && plot.HarvestRemaining > 0)
                {
                    var harvest = Math.Min(plot.HarvestRemaining, Math.Max(0, budget - hours - travel) * crop.HarvestTonnesPerHour);
                    if (harvest > 0)
                    {
                        hours += harvest / crop.HarvestTonnesPerHour + travel; plot.HarvestRemaining -= harvest;
                        var reserve = Math.Min(harvest * crop.SeedShare, Math.Max(0, crop.SeedTonnes * (2 + buildings.Count(p => bio.Plots.GetValueOrDefault(p.Id)?.CropId == id)) - city.Stocks[crop.SeedResource]));
                        city.Stocks[crop.SeedResource] += reserve; city.Stocks[crop.HarvestResource] += harvest - reserve;
                        Add(telemetry.ProductionByResource, crop.SeedResource, reserve); Add(telemetry.ProductionByResource, crop.HarvestResource, harvest - reserve);
                        bio.HarvestedTonnes += harvest; plot.TotalHarvested += harvest; bio.HarvestedCrops.Add(id); plot.FailedSeasons = 0; RecordSoilHarvest(b.Cell, harvest);
                        if (!plot.SeedSaved) { plot.SeedSaved = true; Journal.Record(world, "crop_harvest", b.Id, details: new JsonObject { ["cityId"] = city.Id, ["crop"] = id, ["area"] = plot.Area }); }
                    }
                    plot.HarvestRemaining *= .995;
                    if (plot.HarvestRemaining < .00001)
                    {
                        plot.HarvestRemaining = 0; plot.LastHarvestDay = world.Day; plot.DegreeDays = 0; plot.LastFamily = crop.Family;
                        if (crop.MatureYears == 0) { plot.CropId = null; plot.Area = 0; plot.Phase = "подготовка следующего посева"; }
                        else plot.Phase = "многолетние посадки";
                    }
                }
            }
            if (hours > 0) { spent += hours; life.Tasks.Add(new(b.Id, "cultivate", b.Cell, hours, 0)); Add(life.PracticeHours, "cultivate", hours); Passage(route.Path(b.Cell), 2); }
        }
        if (life.Food is { } food) food.LaborHours += spent;
        return spent;
    }
    private double ProcessCropFood(CityState city, double available, DailyTelemetry telemetry)
    {
        if (BiologyRules is not { } r || available <= 0) return 0;
        var life = State.Cities[city.Id]; var hours = 0d; var prepared = 0d;
        foreach (var crop in r.Crops.Where(c => c.FoodValue > 0).OrderByDescending(c => c.StorageDecay))
        {
            var missing = Math.Max(0, Target(city, "food") - city.Stocks["food"]); if (missing <= 0) break;
            var raw = Math.Min(city.Stocks[crop.HarvestResource], Math.Min(missing / crop.FoodValue, (available - hours) * r.FoodProcessingTonnesPerHour));
            if (raw <= 0) continue; var food = raw * crop.FoodValue; city.Stocks[crop.HarvestResource] -= raw; city.Stocks["food"] += food;
            hours += raw / r.FoodProcessingTonnesPerHour; prepared += food; RecordFoodProduction(city, "cultivate", food);
            Add(life.Production, "food", food); Add(telemetry.IndustrialConsumptionByResource, crop.HarvestResource, raw); Add(telemetry.ProductionByResource, "food", food);
        }
        if (life.Food is { } metrics) { metrics.GardenOutput += prepared; metrics.LaborHours += hours; }
        if (hours > 0) life.Tasks.Add(new("camp:" + city.Id, "food_preparation", Anchor(city), hours, prepared)); return hours;
    }
}
