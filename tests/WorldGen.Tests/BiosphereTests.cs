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
        Assert.Equal(24, bio.Crops.Length); Assert.Equal(8, bio.Animals.Length);
        Assert.All(bio.Crops, c => Assert.Contains(rules.Primitive.Technologies, t => t.Id == c.Technology && t.Prerequisites.Contains("gardening")));
        Assert.All(bio.Animals, a => Assert.Contains(rules.Primitive.Technologies, t => t.Id == a.Technology && t.Prerequisites.Contains("taming")));
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
        herd.Males = 1; var soil = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(origin)].NaturalState; var before = soil.SoilQuality;
        sim.World.Day++; Invoke(d, "TendSpeciesHerds", city, 100d, new DailyTelemetry { Day = sim.World.Day }); Assert.True(herd.Births > 0); Assert.True(soil.SoilQuality > before);
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
