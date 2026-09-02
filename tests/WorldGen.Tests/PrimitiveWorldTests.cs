using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Content;
using WorldGen.Core.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;
using Xunit.Abstractions;

namespace WorldGen.Tests;

public sealed class PrimitiveWorldTests(ITestOutputHelper output)
{
    private static readonly Lazy<Task<(ContentCatalog Content, SphericalWorldDefinition Definition, SphericalEconomyDefinition Economy, SettlementRules Rules, SphericalHydrology Hydro)>> Base = new(async () =>
    {
        var content = await ContentLoader.LoadAsync();
        var definition = await SphericalWorldLoader.LoadAsync(fileName: "spherical-primordial-world.json");
        var economy = await SphericalEconomyLoader.LoadAsync(scenario: "primordial");
        var rules = await SettlementRulesLoader.LoadAsync(scenario: "primordial");
        definition = SphericalSimulation.PrepareWorld(definition, economy);
        var generator = new SphericalTerrainGenerator(definition); var hydro = SphericalHydrology.Build(definition, generator);
        definition = PrimitiveStartLocations.Resolve(definition, generator, hydro);
        return (content, definition, economy, rules, hydro);
    });
    internal static async Task<SphericalSimulation> Create(JsonObject? snapshot = null)
    {
        var b = await Base.Value; var topology = new CubeSphereTopology(b.Definition.FaceSize); var terrain = new SphericalTerrainGenerator(b.Definition);
        return SphericalSimulation.Create(b.Content, b.Definition, b.Economy, topology, terrain, b.Hydro,
            SphericalSettlementLayer.Build(b.Definition, topology, terrain), b.Rules, snapshot);
    }
    [Fact]
    public async Task LocalRoutesCrossOnlyThickColdLakeIceWithoutBuildingsOrPermanentTrails()
    {
        var simulation = await Create(); var d = simulation.Development!;
        var topology = new CubeSphereTopology((await Base.Value).Definition.FaceSize);
        var terrain = simulation.World.Spatial.Territories;
        var lake = terrain.Values.First(t => t.Terrain == "water" && t.Biome != "ocean" && !t.Water.River &&
            topology.GetNeighbors(simulation.Addresses[t.Id]).Any(n => terrain.TryGetValue(SphericalSimulation.ZoneId(n), out var neighbor) && neighbor.Terrain != "water"));
        var water = simulation.Addresses[lake.Id];
        var bank = topology.GetNeighbors(water).First(n => terrain.TryGetValue(SphericalSimulation.ZoneId(n), out var t) && t.Terrain != "water");
        // Exercise the actual private daily route cache without adding a test-only
        // production API. The weather is controlled here, not in a saved world.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(SettlementSimulation);
        var weather = (Dictionary<CellAddress, LocalWeather>)type.GetField("dailyWeather", flags)!.GetValue(d)!;
        var cache = (System.Collections.IDictionary)type.GetField("routes", flags)!.GetValue(d)!;
        bool Reachable()
        {
            cache.Clear(); var tree = type.GetMethod("Routes", flags)!.Invoke(d, [bank])!;
            return ((Dictionary<CellAddress,double>)tree.GetType().GetProperty("Cost")!.GetValue(tree)!).ContainsKey(water);
        }
        weather[water] = weather[water] with { TemperatureC = -8 };
        var ground = d.State.Atmosphere!.Ground[lake.Id]; ground.IceMeters = .05;
        Assert.False(Reachable()); ground.IceMeters = .3;
        Assert.True(d.IcePassable(water)); Assert.True(Reachable());
        Assert.False((bool)type.GetMethod("Free", flags)!.Invoke(d, [water])!);
        var edges = (System.Collections.IDictionary)type.GetField("edges", flags)!.GetValue(d)!;
        var before = edges.Count;
        type.GetMethod("Passage", flags)!.Invoke(d, [new CellAddress[] { bank, water }, 100d]);
        Assert.Equal(before, edges.Count);
        weather[water] = weather[water] with { TemperatureC = 1 };
        Assert.False(d.IcePassable(water)); Assert.False(Reachable());
    }
    [Fact]
    public async Task WeatherOverviewIsBoundedReadOnlyCachedAndReplayable()
    {
        var original = await Create(); original.Advance(3);
        var d = original.Development!; var before = WorldSnapshot.Hash(original.World);
        var first = d.WeatherMap(); Assert.NotNull(first); Assert.Same(first, d.WeatherMap());
        Assert.Equal(before, WorldSnapshot.Hash(original.World));
        var grid = d.State.Atmosphere!.Surface!;
        Assert.InRange(grid.Snow.Length, 24, 6 * 32 * 32);
        Assert.Equal(d.State.Atmosphere.LastDay, grid.LastDay);
        Assert.Equal(8, grid.ClimateResolution);
        Assert.Equal(3, grid.ClimateSampleDays.Sum());
        Assert.Equal(3, grid.LatestClimateSampleDays.Sum());
        Assert.Equal(12 * 6 * 8 * 8, grid.ClimateTemperatureSum.Length);
        Assert.Equal(grid.ClimateTemperatureSum.Length, grid.LatestClimateTemperatureSum.Length);
        Assert.Equal(grid.ClimateTemperatureSum.Length, grid.ClimateWindXSum.Length);
        var restored = await Create(WorldSnapshot.Create(original.World));
        Assert.Equal(before, WorldSnapshot.Hash(restored.World));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(restored.Development!.WeatherMap()));
        original.Advance(2); restored.Advance(2);
        Assert.NotSame(first, d.WeatherMap());
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var day = original.World.Day;
        Assert.Throws<OperationCanceledException>(() => original.Advance(30, cancelled.Token));
        Assert.Equal(day, original.World.Day);
    }
    [Fact]
    public async Task PreciseHydrologyKeepsAllNewHomesOnDryCellsAndRejectsCoarseSnapshots()
    {
        var simulation = await Create(); var b = await Base.Value;
        Assert.Equal(b.Definition.FaceSize, b.Hydro.Resolution);
        Assert.All(simulation.Development!.State.Buildings, building => Assert.False(b.Hydro.IsWater(b.Hydro.Index(building.Cell))));
        Assert.All(simulation.World.Spatial.Territories, pair => Assert.Equal(
            b.Hydro.IsWater(b.Hydro.Index(simulation.Addresses[pair.Key])), pair.Value.Terrain == "water"));
        var coarse = SphericalHydrology.Build(b.Definition, new SphericalTerrainGenerator(b.Definition), 4);
        var topology = new CubeSphereTopology(b.Definition.FaceSize); var terrain = new SphericalTerrainGenerator(b.Definition);
        var old = SphericalSimulation.Create(b.Content, b.Definition, b.Economy, topology, terrain, coarse,
            SphericalSettlementLayer.Build(b.Definition, topology, terrain), b.Rules);
        var snapshot = WorldSnapshot.Create(old.World); var hash = WorldSnapshot.Hash(old.World);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(snapshot));
        Assert.Equal(hash, WorldSnapshot.Hash(old.World));
    }
    [Fact]
    public async Task SixSettlementsShareBasicsButNotBonusesOrWorldKnowledge()
    {
        var simulation = await Create(); var b = await Base.Value;
        Assert.Equal(6, simulation.World.Cities.Count);
        foreach (var city in simulation.World.Cities.Values)
        {
            var life = simulation.Development!.State.Cities[city.Id];
            Assert.All(b.Rules.Primitive!.Technologies.Where(t => t.Baseline), t => Assert.Contains(t.Id, life.Discoveries));
            Assert.DoesNotContain("masonry", life.Discoveries); Assert.Empty(city.Industries);
            Assert.Equal(0, city.Stocks["winter_food"]);
            Assert.True(city.Stocks["stone_kit"] > 0);
            Assert.All(simulation.Development.State.Buildings.Where(h => h.CityId == city.Id && h.Kind == "house"), h => Assert.Equal("clay_straw", h.Lifecycle!.Material));
        }
        Assert.Contains("gardening", simulation.Development!.State.Cities["river_hearth"].Discoveries);
        Assert.DoesNotContain("gardening", simulation.Development.State.Cities["grass_camp"].Discoveries);
        Assert.Equal(0, simulation.Development.State.Cities["grass_camp"].Primitive!.HerdBiomass);
        Assert.All(simulation.World.Cities.Values, c => Assert.Equal(0, c.TechnologyState["water_mill"].Knowledge));
    }
    [Fact]
    public async Task AtLeastOneStartingCultureCanPhysicallyReachWildCotton()
    {
        var simulation = await Create(); var b = await Base.Value;
        var cotton = b.Rules.Primitive!.Biosphere!.Crops.Single(crop => crop.Id == "cotton");
        var topology = new CubeSphereTopology(b.Definition.FaceSize);
        var reachable = simulation.World.Spatial.Territories.Values.Where(territory => territory.AssignedCityId is not null && territory.Terrain != "water")
            .Where(territory => Biosphere.WildScore(cotton.Id, cotton.Habitat, simulation.World.Spatial.Grid.Seed,
                topology.ToUnitVector(simulation.Addresses[territory.Id]), territory.TemperatureC, territory.Moisture, territory.ForestCover) > .22).ToArray();
        Assert.NotEmpty(reachable);
        foreach (var territory in reachable)
        {
            var point = topology.ToUnitVector(simulation.Addresses[territory.Id]);
            var bestWindow = Enumerable.Range(0, simulation.World.Calendar.DaysPerYear).Max(start =>
                Enumerable.Range(0, 18).Sum(step => Math.Max(0, SphericalWeather.SeasonalTemperature(territory.TemperatureC, point,
                    b.Rules.Primitive!.SeasonalAmplitudeC, start + step * 10, simulation.World.Calendar.DaysPerYear) - cotton.BaseTemperature) * 10));
            Assert.True(bestWindow + 1e-6 >= cotton.DegreeDays, $"Дикий хлопок в {territory.Id} не может созреть на поле: {bestWindow:F0} < {cotton.DegreeDays:F0}");
        }
    }
    [Fact]
    public async Task DependentTechnologyStartsPracticeAfterItsPrerequisiteAndStagedMechanicsStayInactive()
    {
        var simulation = await Create(); var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First(); var life = development.State.Cities[city.Id];
        life.Discoveries.Add("horticulture"); life.Discoveries.Add("pottery");
        life.Discoveries.Remove("grow_grape"); life.Discoveries.Remove("winemaking");
        city.TechnologyState["grow_grape"].Knowledge = 0;
        city.TechnologyState["winemaking"].Knowledge = 0;
        life.PracticeHours["cultivate"] = 100_000;
        life.TechnologyPracticeBaselines["grow_grape"] = 0;
        life.TechnologyPracticeBaselines["winemaking"] = 0;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var discover = typeof(SettlementSimulation).GetMethod("DiscoverPrimitive", flags)!;

        discover.Invoke(development, [city]);
        Assert.Contains("grow_grape", life.Discoveries);
        Assert.DoesNotContain("winemaking", life.Discoveries);
        Assert.Equal(100_000, life.TechnologyPracticeBaselines["winemaking"]);

        life.PracticeHours["cultivate"] += 7_200;
        discover.Invoke(development, [city]);
        Assert.Contains("winemaking", life.Discoveries);
        Assert.Equal(0, city.TechnologyState["winemaking"].Capability);
        Assert.Equal(0, city.TechnologyState["winemaking"].Adoption);
    }
    [Fact]
    public async Task SeasonsAndCalendarRespectHemisphereAndDoNotUseWeatherForecast()
    {
        var r = (await Base.Value).Rules.Primitive!;
        var north = UnitVector3.Normalize(.2, 1, 0); var south = new UnitVector3(north.X, -north.Y, 0);
        Assert.Equal(SphericalWeather.SeasonalTemperature(8, north, 9, 0, 365), SphericalWeather.SeasonalTemperature(8, south, 9, 182.5, 365), 9);
        Assert.Equal(8, SphericalWeather.SeasonalTemperature(8, new(1, 0, 0), 9, 0, 365));
        var lateSummer = SphericalWeather.CalendarReserve(r, 8, north, 230, 365);
        var spring = SphericalWeather.CalendarReserve(r, 8, north, 100, 365);
        Assert.True(lateSummer > spring); Assert.InRange(lateSummer, 1, r.WinterReserveDays);
        Assert.Equal(0, SphericalWeather.CalendarReserve(r, 18, new(1, 0, 0), 0, 365));
    }
    [Fact]
    public async Task WeatherIsContinuousAcrossSeamsAndReplayable()
    {
        var r = (await Base.Value).Rules.Primitive!;
        var state = SphericalWeather.Create(r, 19);
        for (var day = 0; day < 100; day++) SphericalWeather.Advance(state, r, 19, day, p => p.Z > 0);
        var copy = JsonSerializer.Deserialize<AtmosphereState>(JsonSerializer.Serialize(state))!;
        for (var day = 100; day < 200; day++) { SphericalWeather.Advance(state, r, 19, day, p => p.Z > 0); SphericalWeather.Advance(copy, r, 19, day, p => p.Z > 0); }
        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(copy));
        var a = UnitVector3.Normalize(1 - 1e-8, .2, 1); var b = UnitVector3.Normalize(1 + 1e-8, .2, 1);
        var wa = SphericalWeather.Sample(state, r, a, 10, 199, 365); var wb = SphericalWeather.Sample(state, r, b, 10, 199, 365);
        Assert.InRange(Math.Abs(wa.TemperatureC - wb.TemperatureC), 0, .00001);
        Assert.InRange(Math.Abs(wa.RainMm - wb.RainMm), 0, .00001);
        Assert.Throws<InvalidOperationException>(() => SphericalWeather.Advance(state, r, 19, 199, _ => false));
    }
    [Fact]
    public void SnowAndSoilWaterAreBoundedAndSeasonControlsGrowth()
    {
        var ground = new GroundWeatherState { SoilWater = .5 };
        var winter = new LocalWeather(-5, 20, .5, .6, .5, 0, 0);
        for (var i = 0; i < 1000; i++) SphericalWeather.WetGround(ground, winter);
        Assert.InRange(ground.Snow, 0, 500); Assert.InRange(ground.SoilWater, 0, 1);
        var summer = winter with { TemperatureC = 20, SoilWater = .6 };
        Assert.True(SphericalWeather.Growth(summer) > SphericalWeather.Growth(winter));
        for (var i = 0; i < 30; i++) SphericalWeather.WetGround(ground, summer);
        Assert.Equal(0, ground.Snow);
    }
    [Fact]
    public async Task SnapshotRetainsWeatherStoresKnowledgeAndNextDaysExactly()
    {
        var original = await Create(); original.Advance(60);
        var restored = await Create(WorldSnapshot.Create(original.World));
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
        original.Advance(20); restored.Advance(20);
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
    }
    [Fact]
    public async Task NoWorkersCannotProduceEquipmentPreserveOrCareForAnimals()
    {
        var simulation = await Create();
        foreach (var city in simulation.World.Cities.Values.ToArray())
        {
            simulation.World.Cities[city.Id] = city with { WorkerShare = 0 };
            city.Stocks["food"] = 0;
        }
        simulation.Advance(1);
        foreach (var city in simulation.World.Cities.Values)
        {
            var life = simulation.Development!.State.Cities[city.Id];
            Assert.Equal(0, life.LaborUsedHours); Assert.Equal(0, life.Primitive!.PreservedToday);
            Assert.Equal(0, life.Primitive.HerdCareHours);
            Assert.Equal(0, life.Production.GetValueOrDefault("stone_kit"));
            Assert.Equal(0, city.Stocks["food"]);
        }
    }
    [Theory]
    [InlineData(1u)]
    [InlineData(71u)]
    [InlineData(811u)]
    public async Task WeatherSeedsCoverBothHemispheresAndStayFinite(uint seed)
    {
        var rules = (await Base.Value).Rules.Primitive!;
        var sky = SphericalWeather.Create(rules, seed);
        Assert.Equal(6, sky.Systems.Count(s => s.Center.Y > 0));
        double northRain = 0, southRain = 0;
        for (var day = 0; day < 730; day++)
        {
            SphericalWeather.Advance(sky, rules, seed, day, p => p.Z > 0);
            northRain += SphericalWeather.Sample(sky, rules, UnitVector3.Normalize(.4, 1, .1), 8, day, 365).RainMm;
            southRain += SphericalWeather.Sample(sky, rules, UnitVector3.Normalize(.4, -1, .1), 8, day, 365).RainMm;
            Assert.All(sky.Systems, s => { Assert.InRange(s.Moisture, 0, 1); Assert.Equal(1, s.Center.Dot(s.Center), 10); });
        }
        Assert.True(northRain > 10); Assert.True(southRain > 10);
    }
    [Fact]
    public async Task CalendarAndFireAreRequiredForPreservationAndNoStockAppearsWithoutThem()
    {
        var simulation = await Create();
        foreach (var city in simulation.World.Cities.Values)
        {
            simulation.Development!.State.Cities[city.Id].Discoveries.Remove("calendar");
            city.TechnologyState["calendar"].Knowledge = 0;
            city.Stocks["food"] = 5;
        }
        simulation.Advance(3);
        Assert.All(simulation.World.Cities.Values, city =>
        {
            Assert.Equal(0, city.Stocks["winter_food"]);
            Assert.Equal(0, simulation.Development!.State.Cities[city.Id].Primitive!.StoredFoodTarget);
        });
    }
    [Fact]
    public async Task FireConsumesActualForestAndCannotJumpDisconnectedPatches()
    {
        var simulation = await Create(); var d = simulation.Development!; var sky = d.State.Atmosphere!;
        sky.Systems.Clear();
        var territory = simulation.World.Spatial.Territories.Values.Where(t => t.Terrain != "water" && t.NaturalState.ForestBiomass > .3).First();
        foreach (var g in sky.Ground.Values) g.SoilWater = .01;
        sky.Ground[territory.Id].Fire = 1;
        var before = territory.NaturalState.ForestBiomass;
        d.RecoverNaturalSites();
        Assert.True(territory.NaturalState.ForestBiomass < before); Assert.True(sky.BurnedTimber > 0);
        var topology = new CubeSphereTopology((await Base.Value).Definition.FaceSize);
        var origin = simulation.Addresses[territory.Id]; var allowed = topology.GetNeighbors(origin).Append(origin).ToHashSet();
        Assert.All(sky.Ground.Where(g => g.Value.Fire > 0), g => Assert.Contains(simulation.Addresses[g.Key], allowed));
    }
    [Fact]
    public async Task TwoYearsRemainFiniteWithinSharedLaborAndFoodStorageBudgets()
    {
        var simulation = await Create(); var observedPreservation = false;
        for (var day = 0; day < 730; day++)
        {
            simulation.Advance(1);
            foreach (var city in simulation.World.Cities.Values)
            {
                var life = simulation.Development!.State.Cities[city.Id]; var p = life.Primitive!;
                Assert.InRange(life.LaborUsedHours, 0, life.LaborAvailableHours + 1e-6);
                Assert.All(city.Stocks.Values, n => Assert.True(double.IsFinite(n) && n >= -1e-8));
                Assert.InRange(p.HerdBiomass, 0, 100);
                Assert.InRange(Math.Abs(city.Stocks["winter_food"] - p.StoredComposition.Amounts.Values.Sum()), 0, 1e-8);
                Assert.InRange(p.Weather!.SoilWater, 0, 1);
                observedPreservation |= p.PreservedToday > 0;
            }
        }
        Assert.True(observedPreservation);
        foreach (var city in simulation.World.Cities.Values)
            output.WriteLine($"{city.Id}: population={simulation.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population}, fresh={city.Stocks["food"]:F3}, winter={city.Stocks["winter_food"]:F3}, health={city.Demography.Health:F2}");
    }
}
