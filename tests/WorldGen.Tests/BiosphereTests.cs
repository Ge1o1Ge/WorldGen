using System.Reflection;
using WorldGen.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class BiosphereTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Invoke(SettlementSimulation d, string method, params object[] args) => typeof(SettlementSimulation).GetMethod(method, Hidden)!.Invoke(d, args);
    private static Dictionary<CellAddress, LocalWeather> Weather(SettlementSimulation d) => (Dictionary<CellAddress, LocalWeather>)typeof(SettlementSimulation).GetField("dailyWeather", Hidden)!.GetValue(d)!;
    [Fact]
    public async Task CatalogExpandsMiniTechnologiesAndRejectsInvalidCoefficients()
    {
        var rules = await SettlementRulesLoader.LoadAsync(scenario: "primordial"); var bio = rules.Primitive!.Biosphere!;
        Assert.Equal(25, bio.Crops.Length); Assert.Equal(8, bio.Animals.Length);
        var cotton = bio.Crops.Single(crop => crop.Id == "cotton");
        Assert.Equal(0, cotton.FoodValue); Assert.Equal("craft", cotton.Domain);
        Assert.Contains(bio.Resources(), resource => resource.Id == cotton.HarvestResource && resource.FoodValue == 0);
        Assert.All(bio.Crops, c => Assert.Contains(rules.Primitive.Technologies, t => t.Id == c.Technology &&
            t.Prerequisites.Contains(c.MatureYears > 0 ? "horticulture" : "gardening")));
        Assert.All(bio.Animals, a => Assert.Contains(rules.Primitive.Technologies, t => t.Id == a.Technology && t.Prerequisites.Contains("taming")));
        Assert.Contains(bio.Animals.Single(a => a.Id == "chicken").ProductRules, p => p.ResourceId == "eggs" && p.FoodValue > 0);
        Assert.Contains(bio.Animals.Single(a => a.Id == "cow").ProductRules, p => p.ResourceId == "milk" && p.Technology == "dairy" && p.LactationDays > 0);
        Assert.Contains(bio.Animals.Single(a => a.Id == "sheep").ProductRules, p => p.ResourceId == "wool" && p.FoodValue == 0);
        Assert.Equal(1, rules.Resources.Count(r => r.Id == "milk"));
        Assert.Contains(rules.Primitive.Processes, p => p.Id == "make_cheese" && p.Inputs.ContainsKey("milk"));
        Assert.Throws<InvalidOperationException>(() => (bio with { FarmingLaborShare = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (bio with { Crops = [.. bio.Crops, bio.Crops[0]] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (bio with { Crops = [bio.Crops[0] with { SeedTonnes = 0 }, .. bio.Crops.Skip(1)] }).Validate());
    }
    [Fact]
    public async Task WildCropIsLocallyCultivableAndRangesAreContinuousAcrossSeams()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!;
        foreach (var cell in sim.Addresses.Values.Where((_, i) => i % 17 == 0))
            foreach (var crop in (CropRule[])Invoke(d, "WildCrops", cell)!) Assert.True((double)Invoke(d, "CropSuitability", crop, cell)! > 0);
        var a = UnitVector3.Normalize(1, .3, 1 - 1e-8); var b = UnitVector3.Normalize(1 - 1e-8, .3, 1);
        foreach (var crop in d.Rules.Primitive!.Biosphere!.Crops) Assert.InRange(Math.Abs(Biosphere.Presence(crop.Id, 271828, a) - Biosphere.Presence(crop.Id, 271828, b)), 0, .000001);
    }
    [Fact]
    public async Task SeedDiscoveryConsumesRealForageAndActualLabor()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"]; var bio = d.State.Cities[city.Id].Biology!;
        var stockBefore = sim.World.Spatial.Territories.Values.Sum(t => d.Stock(t, "forage"));
        Assert.Empty(bio.KnownPlants); Assert.Equal(0d, Invoke(d, "SearchSeeds", city, 0d, new DailyTelemetry { Day = sim.World.Day }));
        var hours = (double)Invoke(d, "SearchSeeds", city, 2d, new DailyTelemetry { Day = sim.World.Day })!;
        Assert.InRange(hours, .000001, 2); Assert.NotEmpty(bio.KnownPlants); Assert.True(bio.SeedCollected > 0);
        Assert.Equal(bio.SeedCollected, stockBefore - sim.World.Spatial.Territories.Values.Sum(t => d.Stock(t, "forage")), 8);
    }
    [Fact]
    public async Task NoSeedsNoSowingAndSmallSeedStockOnlyPlantsANursery()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["elder_camp"]; var life = d.State.Cities[city.Id];
        var origin = (CellAddress)Invoke(d, "Anchor", city)!;
        var crop = ((CropRule[])Invoke(d, "WildCrops", origin)!).First(c => c.MatureYears == 0);
        life.Discoveries.Add("gardening"); life.Discoveries.Add(crop.Technology); life.LaborAvailableHours = 1000;
        var b = new DwellingState { Id = "test-field", CityId = city.Id, Kind = "garden", Cell = origin, ReadyDay = 0, Field = new() }; d.State.Buildings.Add(b);
        Weather(d)[origin] = Weather(d)[origin] with { TemperatureC = 24, SoilWater = 1, Snow = 0 };
        Invoke(d, "FarmCrops", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.Null(life.Biology!.Plots[b.Id].CropId);
        city.Stocks[crop.SeedResource] = crop.SeedTonnes * .1; sim.World.Day++;
        var spent = (double)Invoke(d, "FarmCrops", city, 100d, new DailyTelemetry { Day = sim.World.Day })!;
        var plot = life.Biology.Plots[b.Id]; Assert.Equal(crop.Id, plot.CropId); Assert.Equal(.1, plot.Area, 8);
        Assert.InRange(city.Stocks[crop.SeedResource], 0, 1e-8); Assert.InRange(spent, crop.PlantHours * .1, 100);
        Assert.Equal(0, city.Stocks[crop.HarvestResource]); Assert.Empty(life.Biology.HarvestedCrops);
        plot.HarvestRemaining = .1; sim.World.Day++;
        Invoke(d, "FarmCrops", city, 0d, new DailyTelemetry { Day = sim.World.Day }); Assert.Equal(0, city.Stocks[crop.HarvestResource]);
        sim.World.Day++; Invoke(d, "FarmCrops", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.True(city.Stocks[crop.HarvestResource] > 0); Assert.True(city.Stocks[crop.SeedResource] > 0);
    }
    [Fact]
    public async Task DormancyAndOrchardMaturityDoNotProduceInstantFood()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["elder_camp"]; var life = d.State.Cities[city.Id];
        var origin = (CellAddress)Invoke(d, "Anchor", city)!; var crop = d.Rules.Primitive!.Biosphere!.Crops.Single(c => c.Id == "pear");
        var b = new DwellingState { Id = "test-orchard", CityId = city.Id, Kind = "garden", Cell = origin, ReadyDay = 0, Field = new() }; d.State.Buildings.Add(b);
        life.Biology!.Plots[b.Id] = new() { CropId = crop.Id, Area = 1, DegreeDays = 99999 }; life.LaborAvailableHours = 1000;
        Weather(d)[origin] = Weather(d)[origin] with { TemperatureC = 24, SoilWater = 1, Snow = 0 };
        Invoke(d, "FarmCrops", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.Equal(0, city.Stocks[crop.HarvestResource]);
        Assert.Equal(0, Biosphere.Growth(crop, 24, 0, 0)); Assert.Equal(0, Biosphere.Growth(crop, 24, 1, 20));
        life.Biology.Plots[b.Id].AgeDays = crop.MatureYears * 365; sim.World.Day++;
        Invoke(d, "FarmCrops", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.True(city.Stocks[crop.HarvestResource] > 0);
    }
    [Fact]
    public async Task RotationNeedsHarvestsNotMerelyKnownSpecies()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["river_hearth"]; var life = d.State.Cities[city.Id];
        life.PracticeHours["cultivate"] = 10000; life.Biology!.KnownPlants.UnionWith(["wheat", "barley", "peas"]);
        Invoke(d, "DiscoverPrimitive", city); Assert.DoesNotContain("crop_rotation", life.Discoveries);
        life.Biology.HarvestedCrops.UnionWith(["wheat", "barley", "peas"]); Invoke(d, "DiscoverPrimitive", city); Assert.Contains("crop_rotation", life.Discoveries);
    }
    [Fact]
    public async Task SoilPackageCanReduceYieldEvenWithRotationKnowledge()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["river_hearth"];
        var cell = (CellAddress)Invoke(d, "Anchor", city)!; var territory = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(cell)];
        var crop = d.Rules.Primitive!.Biosphere!.Crops.Single(c => c.Id == "wheat");
        var weather = Weather(d)[cell] with { TemperatureC = 18, SoilWater = .55, Snow = 0 };
        var healthy = new CropPlotState();
        territory.NaturalState.Soil.Nutrients = 1; territory.NaturalState.Soil.OrganicMatter = .8;
        territory.NaturalState.Soil.Rockiness = .05; territory.NaturalState.Soil.Compaction = 0;
        territory.NaturalState.Soil.Pests = 0; territory.NaturalState.Soil.Pathogens[crop.Family] = 0;
        var baseline = (double)Invoke(d, "SoilYieldFactor", crop, cell, healthy, weather)!;

        var depleted = new CropPlotState();
        territory.NaturalState.Soil.Nutrients = .16; territory.NaturalState.Soil.OrganicMatter = .12;
        territory.NaturalState.Soil.Rockiness = .6; territory.NaturalState.Soil.Compaction = .55;
        territory.NaturalState.Soil.Pests = .5; territory.NaturalState.Soil.Pathogens[crop.Family] = .65;
        var damaged = (double)Invoke(d, "SoilYieldFactor", crop, cell, depleted, weather)!;
        Assert.True(damaged < baseline * .4, $"baseline={baseline}, damaged={damaged}");
        Assert.Equal(.5, depleted.PestPressure, 8); Assert.Equal(.65, depleted.DiseasePressure, 8);
    }
    [Fact]
    public async Task ExhaustedPastureMovesAndMineralExtractionGetsRapidlyHarder()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"]; var life = d.State.Cities[city.Id];
        var origin = (CellAddress)Invoke(d, "Anchor", city)!; var cow = d.Rules.Primitive!.Biosphere!.Animals.Single(a => a.Id == "cow");
        d.State.Wildlife!.Clear(); life.Biology!.Herds.Clear(); life.Discoveries.Add("taming"); life.Discoveries.Add(cow.Technology);
        life.Biology.Herds[cow.Id] = new HerdState { Females = 2, Males = 1, Pasture = origin, PastureWork = 24, PastureStartedDay = 0, LastDay = -1 };
        sim.World.Spatial.Territories[SphericalSimulation.ZoneId(origin)].NaturalState.Soil.GrazingBiomass = .01;
        city.Stocks["food"] = city.Stocks["water"] = 100; life.LaborAvailableHours = 1000; sim.World.Day = 400;
        Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day });
        var herd = life.Biology.Herds[cow.Id]; Assert.Contains(origin, herd.PreviousPastures); Assert.NotEqual(origin, herd.Pasture);

        var stone = sim.World.Spatial.Territories.Values.Where(t => t.ResourcePotential.GetValueOrDefault("stone") > .05)
            .OrderByDescending(t => t.ResourcePotential["stone"]).First();
        var before = d.ExtractionDifficulty(stone, "stone"); var capacity = d.Capacity(stone, "stone");
        d.Extract(stone, "stone", capacity * .35); var after = d.ExtractionDifficulty(stone, "stone");
        Assert.True(after > before * 2, $"before={before}, after={after}");
        Assert.True(d.ExtractionDifficulty(stone, "iron_ore") >= 1);
    }
    [Fact]
    public async Task ForesterAndQuarrySpendRealLaborOnTheirLocalNaturalSites()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"]; var life = d.State.Cities[city.Id];
        var origin = (CellAddress)Invoke(d, "Anchor", city)!;
        foreach (var territory in sim.World.Spatial.Territories.Values.Where(t => t.AssignedCityId == city.Id))
            d.Extract(territory, "timber", d.Stock(territory, "timber") * .7);
        d.State.Buildings.Add(new DwellingState { Id = "test-forester", CityId = city.Id, Kind = "forester_lodge", Cell = origin, Status = "active", ReadyDay = 0 });
        life.Discoveries.Add("forestry"); life.LaborAvailableHours = 1000;
        var forestrySpent = (double)Invoke(d, "RunManagedLandSites", city, 100d, new DailyTelemetry { Day = sim.World.Day })!;
        Assert.True(forestrySpent > 0); Assert.Contains(sim.World.Spatial.Territories.Values, t => t.NaturalState.ManagedForestCare > 0);

        var stone = sim.World.Spatial.Territories.Values.Where(t => t.ResourcePotential.GetValueOrDefault("stone") > .05)
            .OrderByDescending(t => t.ResourcePotential["stone"]).First();
        var quarryCell = sim.Addresses[stone.Id];
        d.State.Buildings.Add(new DwellingState { Id = "test-quarry", CityId = city.Id, Kind = "quarry", Cell = quarryCell, Status = "active", ReadyDay = 0 });
        life.Discoveries.Add("quarrying"); city.Stocks["stone"] = 0; var before = d.Stock(stone, "stone");
        var quarrySpent = (double)Invoke(d, "RunManagedLandSites", city, 100d, new DailyTelemetry { Day = sim.World.Day })!;
        Assert.True(quarrySpent > 0); Assert.True(city.Stocks["stone"] > 0); Assert.True(d.Stock(stone, "stone") < before);
    }
    [Fact]
    public async Task AlternativeAnimalPrerequisiteNeedsOnlyOneSuitableSpeciesAndResetsPracticeWhileBlocked()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"];
        var life = d.State.Cities[city.Id]; life.Discoveries.Remove("dairy");
        life.Discoveries.Add("taming"); life.Discoveries.Add("herd_chicken"); life.PracticeHours["herd"] = 10000;
        city.TechnologyState["dairy"].Knowledge = 0;
        Invoke(d, "DiscoverPrimitive", city);
        Assert.DoesNotContain("dairy", life.Discoveries);
        Assert.Equal(10000, life.TechnologyPracticeBaselines["dairy"]);

        life.Discoveries.Add("herd_goat"); life.PracticeHours["herd"] += 4799;
        Invoke(d, "DiscoverPrimitive", city); Assert.DoesNotContain("dairy", life.Discoveries);
        life.PracticeHours["herd"]++;
        Invoke(d, "DiscoverPrimitive", city); Assert.Contains("dairy", life.Discoveries);
    }
    [Fact]
    public async Task CaptureRemovesWildBiomassAndBreedingRequiresBothSexes()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"]; var life = d.State.Cities[city.Id]; var bio = life.Biology!;
        Assert.Empty(bio.Herds); Assert.Equal(0, life.Primitive!.HerdBiomass);
        var origin = (CellAddress)Invoke(d, "Anchor", city)!; var animal = d.Rules.Primitive!.Biosphere!.Animals.Single(a => a.Id == "chicken");
        d.State.Wildlife!.Clear(); var wild = new WildlifeGroupState { Id = "test-flock", Center = origin, Capacity = 1, Biomass = 1 }; wild.SpeciesId = animal.Id; d.State.Wildlife.Add(wild);
        life.LaborAvailableHours = 1000; city.Stocks["food"] = 10; city.Stocks["water"] = 10;
        Invoke(d, "TendSpeciesHerds", city, 0d, new DailyTelemetry { Day = sim.World.Day }); Assert.Equal(1, wild.Biomass);
        sim.World.Day++; Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day });
        var herd = bio.Herds[animal.Id]; Assert.Equal(1, herd.Captured); Assert.Equal(1 - animal.BodyTonnes, wild.Biomass, 8); Assert.Equal(1, herd.Females); Assert.Equal(0, herd.Males);
        d.State.Wildlife.Clear(); life.Discoveries.Add(animal.Technology); herd.Pasture = origin; herd.PastureWork = 24; herd.PregnancyDays = animal.GestationDays - 1;
        sim.World.Day++; Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.Equal(0, herd.Births);
        Assert.True(city.Stocks["eggs"] > 0); Assert.True(herd.ProductsToday.GetValueOrDefault("eggs") > 0);
        herd.Males = 1; var soil = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(origin)].NaturalState; var before = soil.SoilQuality;
        sim.World.Day++; Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.True(herd.Births > 0); Assert.True(soil.SoilQuality > before);
    }

    [Fact]
    public async Task DifferentSpeciesDoNotPrepareTheSamePastureCell()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"];
        var life = d.State.Cities[city.Id]; var origin = (CellAddress)Invoke(d, "Anchor", city)!;
        life.Biology!.Herds.Clear(); life.Biology.Herds["chicken"] = new() { Females = 1, Pasture = origin, PastureWork = 24 };
        life.Biology.Herds["rabbit"] = new() { Females = 1, Pasture = origin, PastureWork = 24 };
        life.LaborAvailableHours = 1000; city.Stocks["food"] = city.Stocks["water"] = 100;

        Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day });

        var occupied = life.Biology.Herds.Values.Where(herd => herd.Count > 0 && herd.Pasture is not null).Select(herd => herd.Pasture!.Value).ToArray();
        Assert.Equal(occupied.Length, occupied.Distinct().Count());
    }
    [Fact]
    public async Task MilkNeedsKnowledgeRecentBirthAndPaidLaborThenCanBeEatenAsItsOwnStock()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"]; var life = d.State.Cities[city.Id];
        var origin = (CellAddress)Invoke(d, "Anchor", city)!; var cow = d.Rules.Primitive!.Biosphere!.Animals.Single(a => a.Id == "cow");
        life.Biology!.Herds.Clear(); var herd = new HerdState { Females = 2, Males = 1, Pasture = origin, PastureWork = 24, LastBirthDay = sim.World.Day };
        life.Biology.Herds[cow.Id] = herd; life.Discoveries.Add(cow.Technology); life.LaborAvailableHours = 1000; city.Stocks["water"] = city.Stocks["food"] = 10;
        Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.Equal(0, city.Stocks["milk"]);
        life.Discoveries.Add("dairy"); sim.World.Day++;
        var telemetry = new DailyTelemetry { Day = sim.World.Day };
        var spent = (double)Invoke(d, "TendSpeciesHerds", city, 100d, telemetry)!;
        Assert.True(spent > 0); Assert.True(city.Stocks["milk"] > 0); Assert.True(herd.ProductsToday["milk"] > 0);
        var milk = city.Stocks["milk"]; var eaten = d.ConsumeEdibleStocks(city, milk * .5, telemetry);
        Assert.True(eaten > 0); Assert.True(city.Stocks["milk"] < milk); Assert.True(telemetry.HouseholdConsumptionByResource["milk"] > 0);
    }
    [Fact]
    public async Task DataDefinedProcessPaysInputsEquipmentAndLaborAndPersistsProgress()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["elder_camp"]; var life = d.State.Cities[city.Id];
        life.Discoveries.Add("pottery"); city.Stocks["clay"] = city.Stocks["firewood"] = city.Stocks["stone_kit"] = 1; city.Stocks["pottery_ware"] = 0;
        var telemetry = new DailyTelemetry { Day = sim.World.Day }; var clay = city.Stocks["clay"];
        var spent = (double)Invoke(d, "RunPrimitiveProcesses", city, 100d, telemetry)!;
        Assert.True(spent > 0); Assert.True(city.Stocks["pottery_ware"] > 0); Assert.True(city.Stocks["clay"] < clay);
        var state = life.Processes["open_fire_pottery"];
        Assert.True(state.BatchesToday > 0); Assert.Equal(spent, state.LaborHoursToday, 8); Assert.Equal(state.BatchesToday, state.TotalBatches, 8);
        city.Stocks["pottery_ware"] = 0; city.Stocks["stone_kit"] = 0; sim.World.Day++;
        Assert.Equal(0, (double)Invoke(d, "RunPrimitiveProcesses", city, 100d, new DailyTelemetry { Day = sim.World.Day })!);
        Assert.Equal("equipment:stone_kit", state.Constraint); Assert.Equal(0, city.Stocks["pottery_ware"]);
    }
    [Fact]
    public async Task AbandonedTrialCannotLeaveCouncilBlockedInObservationForever()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["river_hearth"]; var life = d.State.Cities[city.Id];
        var cell = (CellAddress)Invoke(d, "Anchor", city)!;
        d.State.Buildings.Add(new() { Id = "failed-trial", CityId = city.Id, Kind = "garden", Cell = cell, Status = "abandoned", ReadyDay = 0 });
        var proposal = new CollectiveProposal { Id = "trial", Key = "prepare-garden:2", Scope = city.Id, Domain = "food", Kind = "garden", Reason = "test", Phase = "observing", BuildingId = "failed-trial", FinishedDay = 0 };
        life.Council!.Proposals.Add(proposal); Invoke(d, "ObserveCouncilResults", city, life.Council, d.Rules.Decisions!);
        Assert.Equal("uncertain", proposal.Phase); Assert.Null(proposal.Outcome);
    }
    [Fact]
    public async Task CatalogSpeciesHaveActualHabitatOnThePlanet()
    {
        var definition = await SphericalWorldLoader.LoadAsync(fileName: "spherical-primordial-world.json");
        var generator = new SphericalTerrainGenerator(definition); var topology = new CubeSphereTopology(definition.FaceSize);
        var hydro = SphericalHydrology.Build(definition, generator);
        var rules = (await SettlementRulesLoader.LoadAsync(scenario: "primordial")).Primitive!.Biosphere!;
        var seen = new HashSet<string>();
        foreach (var face in Enum.GetValues<CubeFace>()) for (var y = 0; y < definition.FaceSize; y += 16) for (var x = 0; x < definition.FaceSize; x += 16)
        {
            var cell = new CellAddress(face, x, y); if (hydro.IsWater(hydro.Index(cell))) continue;
            var point = topology.ToUnitVector(cell); var t = generator.SampleSurface(point);
            double Score(string id, HabitatRule habitat) => Biosphere.WildScore(id, habitat, definition.Seed, point, t.TemperatureC, t.Moisture, t.ForestCover);
            foreach (var crop in rules.Crops.OrderByDescending(c => Score(c.Id, c.Habitat)).Where(c => Score(c.Id, c.Habitat) > .22)) seen.Add(crop.Id);
            var animal = rules.Animals.OrderByDescending(a => Score(a.Id, a.Habitat)).FirstOrDefault(a => Score(a.Id, a.Habitat) > .1);
            if (animal is not null) seen.Add(animal.Id);
        }
        Assert.All(rules.Crops.Select(c => c.Id).Concat(rules.Animals.Select(a => a.Id)), id => Assert.Contains(id, seen));
    }
    [Fact]
    public async Task DepletedHomeWoodTriggersSurveyCampAndNeutralLandSurvivesReplay()
    {
        var sim = await PrimitiveWorldTests.Create(); var d = sim.Development!; var city = sim.World.Cities["grass_camp"];
        foreach (var t in sim.World.Spatial.Territories.Values.Where(t => t.AssignedCityId == city.Id)) d.Extract(t, "timber", d.Stock(t, "timber"));
        city.Stocks["timber"] = city.Stocks["firewood"] = 0; city.Stocks["food"] = 10; city.Stocks["water"] = 10;
        Assert.True((bool)Invoke(d, "RemoteWoodPressure", city)!); var zones = sim.Addresses.Count;
        sim.Advance(120);
        var bio = d.State.Cities[city.Id].Biology!;
        Assert.NotEmpty(bio.Camps); Assert.True(bio.CampTimberDelivered > 0); Assert.True(sim.Addresses.Count > zones);
        Assert.Contains(sim.World.Spatial.Territories.Values, t => t.AssignedCityId == "" && t.Population == 0);
        var restored = await PrimitiveWorldTests.Create(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(3); restored.Advance(3); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }
}
