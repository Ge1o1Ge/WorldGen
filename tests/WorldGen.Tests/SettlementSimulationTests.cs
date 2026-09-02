using WorldGen.Content;
using WorldGen.Core.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;
using Xunit.Abstractions;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests(ITestOutputHelper output)
{
    private static readonly Lazy<Task<(ContentCatalog, SphericalWorldDefinition, SphericalEconomyDefinition, SettlementRules, SphericalHydrology)>> Base = new(async () =>
    {
        var content = await ContentLoader.LoadAsync(); var definition = await SphericalWorldLoader.LoadAsync();
        var economy = await SphericalEconomyLoader.LoadAsync(scenario: "foragers");
        return (content, SphericalSimulation.PrepareWorld(definition, economy), economy, await SettlementRulesLoader.LoadAsync(),
            // Historical forager fixtures have fixed sites and calibrated supply
            // assumptions on the v1 coarse grid. PrimitiveWorldTests exercises
            // the new 1:1 world; keep these mechanism regressions reproducible.
            SphericalHydrology.Build(definition, new SphericalTerrainGenerator(definition), stride: 4));
    });
    private static async Task<(SphericalSimulation Simulation, SphericalSettlementLayer Layer, CubeSphereTopology Topology)> Create(bool disableWells = false, bool disableExploration = false)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        if (disableExploration) rules = rules with { Exploration = null };
        if (disableWells) rules = rules with { Wellbeing = null, Lifecycle = null, Discoveries = rules.Discoveries.Select(d => d.Id == "well" ? d with { PracticeHours = 1e12 } : d).ToArray() };
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);
        return (SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, layer, rules), layer, topology);
    }

    [Fact]
    public async Task EarlierScenarioDoesNotLoadInstallationsWhoseEraResourcesAreAbsent()
    {
        var rules = await SettlementRulesLoader.LoadAsync();
        Assert.DoesNotContain(rules.Buildings, building => building.Technology is not null);
        Assert.DoesNotContain(rules.Buildings, building => building.Id is "water_mill" or "windmill" or "animal_mill");
    }

    [Fact]
    public async Task SphericalStartupRestorePreservesWorldAndSubsequentSteps()
    {
        var (original, _, _) = await Create();
        original.Advance(60);
        var (content, definition, economy, rules, hydro) = await Base.Value;
        var topology = new CubeSphereTopology(definition.FaceSize);
        var terrain = new SphericalTerrainGenerator(definition);
        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);
        var restored = SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, layer, rules, WorldSnapshot.Create(original.World));
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
        Assert.All(restored.Development!.State.Buildings, b => Assert.Equal(b.Status != "demolished" && !(b.Kind == "garden" && b.Status == "abandoned"), layer.Construction.Buildings.ContainsKey(b.Id)));
        original.Advance(12); restored.Advance(12);
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task ChangedRulesRejectOldSnapshotInsteadOfMigratingIt()
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        var oldLayer = SphericalSettlementLayer.Build(definition, topology, terrain);
        var old = SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, oldLayer, rules with { Trails = null });
        old.Advance(3); var snapshot = WorldSnapshot.Create(old.World); var beforeHash = WorldSnapshot.Hash(old.World);

        Assert.Throws<InvalidOperationException>(() => SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro,
            SphericalSettlementLayer.Build(definition, topology, terrain), rules, snapshot));
        Assert.Equal(beforeHash, WorldSnapshot.Hash(old.World));
    }

    [Fact]
    public void WeakTrailsRegrowFasterThanEstablishedPathsAndWetForestsRegrowFasterThanDryLand()
    {
        var rules = new SettlementTrailRules(); rules.Validate();
        var weak = .1; var strong = .9; var dry = .1;
        for (var day = 0; day < 60; day++)
        { weak = rules.Decay(weak, .8, .8); strong = rules.Decay(strong, .8, .8); dry = rules.Decay(dry, .1, 0); }
        Assert.True(weak < .025); Assert.True(strong > .4); Assert.True(dry > weak);
        Assert.Throws<InvalidOperationException>(() => (rules with { WeakHalfLifeDays = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { ForgetBelow = .5 }).Validate());
    }

    [Fact]
    public async Task InitialHousingConservesPopulationAndUsesAtMostFourSlots()
    {
        var (simulation, layer, _) = await Create(); var state = simulation.Development!.State;
        Assert.Equal(490, state.Buildings.Sum(b => b.Residents));
        Assert.All(state.Buildings, b => Assert.InRange(b.Residents, 0, 25));
        Assert.All(state.Buildings.GroupBy(b => b.Cell), g => Assert.True(layer.Construction.GetOccupiedCapacity(g.Key) <= 4));
        Assert.All(simulation.World.Cities.Values, city => Assert.Empty(city.Industries));
        Assert.All(simulation.World.Cities.Values.SelectMany(c => c.TechnologyState.Values), t => Assert.Equal(0, t.Adoption));
    }

    [Fact]
    public async Task StandingWaterRuinsBuildingsAndRelocatesAFloodedCenterAfterFiveDays()
    {
        var (content, definition, economy, rules, _) = await Base.Value;
        var topology = new CubeSphereTopology(definition.FaceSize);
        var terrain = new SphericalTerrainGenerator(definition);
        var hydro = SphericalHydrology.Build(definition, terrain);
        var water = SurfaceWaterState.FromHydrology(hydro, definition.ZoneSizeMeters * definition.ZoneSizeMeters);
        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);
        var simulation = SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, layer, rules,
            surfaceWater: water);
        var city = simulation.World.Cities.Values.OrderBy(value => value.Id, StringComparer.Ordinal).First();
        var node = simulation.World.Spatial.Nodes[city.SpatialNodeId];
        var oldCenter = simulation.Addresses[node.AnchorTerritoryId!];
        var victim = simulation.Development!.State.Buildings.First(building =>
            building.CityId == city.Id && building.Kind == "house" && building.Status == "active");
        var floodedLandCell = simulation.Addresses.Where(pair => simulation.World.Spatial.Territories[pair.Key].AssignedCityId == city.Id)
            .Select(pair => pair.Value).First(cell => cell != oldCenter && cell != victim.Cell);
        layer.UpsertLand(new UsedLandParcel("flood-test-field", city.Id, floodedLandCell,
            CityAssetKind.CultivatedField, 1, .1f));
        water.Depth[water.Index(oldCenter)] = 1;
        water.Depth[water.Index(victim.Cell)] = 1;
        water.Depth[water.Index(floodedLandCell)] = 1;

        simulation.Advance(4);

        Assert.Equal("active", victim.Status);
        Assert.Equal(4, victim.FloodedDays);
        simulation.Advance(1);

        Assert.Equal("abandoned", victim.Status);
        Assert.Equal(0, layer.UsedLands.Single(land => land.Id == "flood-test-field").Usage);
        Assert.Equal(0, simulation.World.Spatial.Territories[SphericalSimulation.ZoneId(victim.Cell)].NaturalState.ForestBiomass);
        Assert.NotEqual(node.AnchorTerritoryId, simulation.World.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId);
        Assert.Contains(simulation.World.Journal, entry => entry.Type == "settlement_center_relocated");
    }

    [Fact]
    public async Task ForagersSurviveOneYearWithFiniteLaborAndObservableDevelopment()
    {
        var (simulation, _, _) = await Create(); var development = simulation.Development!;
        for (var day = 0; day < 365; day++)
        {
            simulation.Advance(1);
            foreach (var city in simulation.World.Cities.Values)
            {
                var life = development.State.Cities[city.Id];
                Assert.True(life.LaborUsedHours + life.IndustryLaborHours <= life.LaborAvailableHours + 1e-5);
                Assert.All(city.Stocks.Values, stock => Assert.True(double.IsFinite(stock) && stock >= -1e-8));
                Assert.Equal(simulation.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population,
                    development.State.Buildings.Where(b => b.CityId == city.Id).Sum(b => b.Residents) + life.Unhoused);
            }
        }
        foreach (var city in simulation.World.Cities.Values)
        {
            var life = development.State.Cities[city.Id];
            output.WriteLine($"{city.Id}: population={simulation.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population}, food={city.Stocks["food"]:F3}, water={life.WaterCoverage:F2}, shortageDays={city.Shortage.Days}, discoveries={string.Join(',', life.Discoveries)}, housing={life.HousingCapacity}, decision={life.Decision}");
            Assert.True(life.WaterCoverage > .9, $"Нет воды в {city.Id}");
            Assert.False(city.Shortage.Active, $"Пищевой коллапс в {city.Id}");
        }
        Assert.NotEmpty(development.State.Trails);
        Assert.Empty(simulation.World.TradeIntents);
        Assert.Contains(simulation.World.Journal, e => e.Type == "household_discovery");
        Assert.Contains(simulation.World.Journal, e => e.Type == "settlement_building_completed");
    }

    [Fact]
    public async Task ExtractionNeverCreatesResourcesAndOnlyRenewablePoolsRecover()
    {
        var (simulation, _, _) = await Create(); var development = simulation.Development!;
        var forest = simulation.World.Spatial.Territories.Values.First(t => t.Terrain == "land" && t.ForestCover > .4);
        var timber = development.Stock(forest, "timber");
        Assert.Equal(timber, development.Extract(forest, "timber", timber + 100));
        Assert.Equal(0, development.Extract(forest, "timber", 1));
        var recipe = simulation.Content.Recipes.Recipes.First(r => r.SitePotential == "timber");
        Assert.Equal(0, development.LimitIndustry(forest, recipe, 10));
        var rock = simulation.World.Spatial.Territories.Values.First(t => t.ResourcePotential["stone"] > 0);
        development.Extract(rock, "stone", double.MaxValue);
        development.RecoverNaturalSites();
        Assert.True(development.Stock(forest, "timber") > 0);
        Assert.Equal(0, development.Stock(rock, "stone"));
        Assert.Throws<ArgumentOutOfRangeException>(() => development.Extract(forest, "timber", double.NaN));
    }

    [Fact]
    public async Task DuplicateRunsAndSnapshotRoundTripPreserveHouseholds()
    {
        var (a, _, _) = await Create(); var (b, _, _) = await Create();
        a.Advance(37); b.Advance(37);
        Assert.Equal(WorldSnapshot.Hash(a.World), WorldSnapshot.Hash(b.World));
        var snapshot = WorldSnapshot.Create(a.World); var restored = WorldSnapshot.Restore(a.Content, snapshot);
        Assert.Equal(WorldSnapshot.Hash(a.World), WorldSnapshot.Hash(restored));
        var (content, definition, economy, rules, hydro) = await Base.Value;
        var topology = new CubeSphereTopology(definition.FaceSize);
        var layer = SphericalSettlementLayer.Build(definition, topology, new SphericalTerrainGenerator(definition));
        // The full spherical host supplies the same off-footprint terrain resolver to scouts.
        var continued = SphericalSimulation.Create(content, definition, economy, topology, new SphericalTerrainGenerator(definition), hydro, layer, rules, snapshot);
        for (var i = 0; i < 15; i++) { a.Advance(1); continued.Advance(1); }
        Assert.Equal(WorldSnapshot.Hash(a.World), WorldSnapshot.Hash(continued.World));
    }

    [Fact]
    public async Task NoWorkersMeansNoHouseholdProductionOrFreeConstruction()
    {
        var (simulation, _, _) = await Create();
        foreach (var city in simulation.World.Cities.Values.ToArray()) simulation.World.Cities[city.Id] = city with { WorkerShare = 0 };
        simulation.Advance(5);
        Assert.All(simulation.Development!.State.Cities.Values, life =>
        {
            Assert.Equal(0, life.LaborUsedHours);
            Assert.All(life.Production.Values, amount => Assert.Equal(0, amount));
        });
        Assert.DoesNotContain(simulation.World.Journal, e => e.Type == "settlement_building_completed");
        Assert.All(simulation.Development.State.Buildings.Where(b => b.Status == "building"), b => Assert.Equal(0, b.LaborDone));
    }

    [Fact]
    public async Task AUsefulWellCostsMaterialsAndReducesWaterTravel()
    {
        var (simulation, _, _) = await Create(); var development = simulation.Development!;
        simulation.Advance(3);
        var before = development.State.Cities.ToDictionary(p => p.Key, p => p.Value.WaterTravelHours / Math.Max(.001, p.Value.WaterCollected));
        foreach (var life in development.State.Cities.Values) life.Discoveries.Add("well");
        simulation.Advance(20);
        var well = Assert.Single(development.State.Buildings, b => b.Kind == "well" && b.Status == "active" && b.CityId == "river_hearth");
        var after = development.State.Cities[well.CityId];
        Assert.True(after.WaterTravelHours / Math.Max(.001, after.WaterCollected) < before[well.CityId] * .7);
        Assert.True(well.LaborDone >= development.Rules.Buildings.Single(b => b.Id == "well").LaborHours);
        Assert.True(simulation.World.Telemetry.Daily.Sum(t => t.InfrastructureConsumptionByResource.GetValueOrDefault("timber")) >= .2);
    }

    [Fact]
    public async Task LocalMovementCreatesOnlyAdjacentLandEdgesAndUnusedTrailsFade()
    {
        var (simulation, _, topology) = await Create(disableExploration: true); simulation.Advance(15);
        var state = simulation.Development!.State;
        Assert.NotEmpty(state.Trails);
        foreach (var edge in state.Trails)
        {
            Assert.Contains(edge.To, topology.GetNeighbors(edge.From));
            Assert.Equal("land", simulation.World.Spatial.Territories[SphericalSimulation.ZoneId(edge.To)].Terrain);
        }
        var before = state.Trails.Sum(t => t.Strength);
        var countBefore = state.Trails.Count;
        foreach (var city in simulation.World.Cities.Values.ToArray()) simulation.World.Cities[city.Id] = city with { WorkerShare = 0 };
        simulation.Advance(5);
        Assert.True(state.Trails.Sum(t => t.Strength) < before);
        simulation.Advance(180);
        Assert.True(state.Trails.Count < countBefore);
        Assert.All(state.Trails, edge => Assert.Equal(0, edge.Passages));
    }

    [Fact]
    public async Task PersistentWaterDistanceCanCausePaidRelocationWhileVacatedHouseKeepsItsFootprint()
    {
        var (simulation, layer, _) = await Create(disableWells: true);
        simulation.Advance(100);
        var relocations = simulation.World.Journal.Where(e => e.Type == "household_relocated").ToArray();
        Assert.NotEmpty(relocations);
        foreach (var relocation in relocations)
        {
            var sourceId = relocation.Details["from"]!.GetValue<string>();
            var vacated = simulation.Development!.State.Buildings.Single(b => b.Id == sourceId);
            Assert.InRange(vacated.Residents, relocation.Details["remainingResidents"]!.GetValue<int>(), simulation.Development.Rules.ResidentsPerHouse);
            Assert.Equal("active", vacated.Status);
            if (vacated.Residents == 0) Assert.True(vacated.UnusedDays > 0);
            Assert.Equal("house", layer.Construction.Buildings[vacated.Id].BuildingTypeId);
            Assert.True(layer.Construction.Buildings[vacated.Id].InfluenceStrength > 0);
            Assert.True(layer.Construction.GetOccupiedCapacity(vacated.Cell) > 0);
        }
    }

    [Fact]
    public async Task RulesRejectRecoveryForNonrenewablePoolsAndUnknownActivityReferences()
    {
        var (_, _, _, rules, _) = await Base.Value;
        Assert.Throws<InvalidOperationException>(() => (rules with { NaturalPools = rules.NaturalPools.Select(p => p.Id == "iron_ore" ? p with { RecoveryPerDay = .1 } : p).ToArray() }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { Activities = [rules.Activities[0] with { Pool = "missing" }] }).Validate());
    }

    [Fact]
    public async Task HouseholdsAndIndustriesShareLaborInTheEarlierScenario()
    {
        var (content, _, _, rules, hydro) = await Base.Value;
        var definition = await SphericalWorldLoader.LoadAsync(); var economy = await SphericalEconomyLoader.LoadAsync();
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);
        var simulation = SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, layer, rules);
        for (var day = 0; day < 7; day++)
        {
            simulation.Advance(1);
            foreach (var life in simulation.Development!.State.Cities.Values)
                Assert.True(life.LaborUsedHours + life.IndustryLaborHours <= life.LaborAvailableHours + 1e-4);
        }
        Assert.Contains(simulation.World.Cities.Values.SelectMany(c => c.Industries), i => i.TotalBatches > 0);
    }
}
