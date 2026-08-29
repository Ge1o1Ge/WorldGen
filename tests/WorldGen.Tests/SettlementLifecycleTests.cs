using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    private static async Task<SphericalSimulation> CreateLifecycle(JsonObject? snapshot = null, bool legacy = false, Func<SettlementLifecycleRules, SettlementLifecycleRules>? edit = null)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        rules = rules with { Wellbeing = null, Lifecycle = legacy ? null : edit?.Invoke(rules.Lifecycle!) ?? rules.Lifecycle };
        var topology = new CubeSphereTopology(definition.FaceSize); var generator = new SphericalTerrainGenerator(definition);
        return SphericalSimulation.Create(content, definition, economy, topology, generator, hydro,
            SphericalSettlementLayer.Build(definition, topology, generator), rules, snapshot);
    }

    [Fact]
    public async Task LifecycleWearHasGraceAndAnIrreversibleFloorEvenWithPerfectRepairs()
    {
        var (_, _, _, rules, _) = await Base.Value; var materials = rules.Lifecycle!.Materials;
        var wood = materials.Single(m => m.Id == "wood");
        var age = new BuildingLifecycleState { Material = wood.Id };
        age.Age(wood, 730); Assert.Equal(0, age.RepairableWear); Assert.Equal(0, age.PermanentWear);
        age.Age(wood, 1095); Assert.Equal(.15, age.RepairableWear, 9); Assert.Equal(.05, age.PermanentWear, 9);
        age.RepairableWear = 0; Assert.Equal(.95, age.Efficiency, 9);
        age.Age(wood, 1095); Assert.Equal(.05, age.PermanentWear, 9); // same day cannot age twice
        var idle = new BuildingLifecycleState { Material = wood.Id, AgeDays = 730, LastAgedDay = 730 };
        idle.Age(wood, 1095, rules.Lifecycle.UnusedWearMultiplier);
        Assert.Equal(.225, idle.RepairableWear, 9); Assert.Equal(.05, idle.PermanentWear, 9);
        foreach (var material in materials)
        {
            var maintained = new BuildingLifecycleState { Material = material.Id };
            for (var day = 1; day <= material.ServiceLifeDays; day++) { maintained.Age(material, day); maintained.RepairableWear = 0; }
            Assert.Equal(0, maintained.Efficiency); Assert.Equal(1, maintained.PermanentWear);
        }
        Assert.True(materials.Single(m => m.Id == "clay_straw").LaborMultiplier < wood.LaborMultiplier);
        Assert.True(materials.Single(m => m.Id == "stone").LaborMultiplier > wood.LaborMultiplier);
        Assert.Throws<InvalidOperationException>(() => (rules.Lifecycle with { WellCapacity = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules.Lifecycle with { MaintenanceLaborShare = 1.1 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules.Lifecycle with { UnusedWearMultiplier = .9 }).Validate());
    }

    [Fact]
    public async Task LifecycleRepairPaysMaterialsAndLaborWithoutRepairingAge()
    {
        var sim = await CreateLifecycle(); var state = sim.Development!.State;
        var house = state.Buildings.First(); var age = house.Lifecycle!;
        age.AgeDays = 1095; age.RepairableWear = .15; age.PermanentWear = .05;
        var city = sim.World.Cities[house.CityId]; city.Stocks["timber"] = 2;
        sim.Advance(1);
        Assert.Equal(0, age.RepairableWear, 9); Assert.Equal(.05, age.PermanentWear, 9); Assert.Equal(1095, age.AgeDays);
        var m = state.Cities[city.Id].Maintenance!;
        Assert.Equal(.018, m.MaterialsUsed["timber"], 9); Assert.True(m.RepairHours >= 18);
        Assert.True(sim.World.Telemetry.Daily.Last().InfrastructureConsumptionByResource["timber"] >= .018);
        Assert.True(city.Infrastructure.HousingCondition < 1);
        Assert.Equal("active", house.Status); Assert.True(house.Residents > 0);
    }

    [Fact]
    public async Task LifecycleNoWorkersCannotRepairDemolishOrExtractWater()
    {
        var sim = await CreateLifecycle(); var state = sim.Development!.State;
        var house = state.Buildings.First(); house.Lifecycle!.RepairableWear = .2;
        var ruin = state.Buildings.Last(); ruin.Status = "abandoned"; ruin.Residents = 0;
        foreach (var city in sim.World.Cities.Values.ToArray()) sim.World.Cities[city.Id] = city with { WorkerShare = 0 };
        sim.Development.RehousePopulation();
        sim.Advance(1);
        Assert.Equal(.2, house.Lifecycle.RepairableWear); Assert.Equal("abandoned", ruin.Status); Assert.Equal(0, ruin.Lifecycle!.DemolitionDone);
        Assert.All(state.Cities.Values, life => { Assert.Equal(0, life.Maintenance!.RepairHours); Assert.Equal(0, life.Maintenance.DemolitionHours); Assert.Equal(0, life.WaterCollected); });
    }

    [Fact]
    public async Task LifecycleReplacementMovesGraduallyAndPaidDemolitionFreesTheOldSlot()
    {
        var sim = await CreateLifecycle(); sim.Advance(3); var state = sim.Development!.State;
        var source = state.Buildings.First(b => b.CityId == "river_hearth" && b.Residents == 25);
        source.Lifecycle!.AgeDays = 3100; source.Lifecycle.PermanentWear = .4;
        var population = state.Buildings.Sum(b => b.Residents) + state.Cities.Values.Sum(c => c.Unhoused);
        DwellingState? target = null;
        for (var i = 0; i < 100; i++)
        {
            var beforeResidents = source.Residents;
            sim.Advance(1); target = state.Buildings.FirstOrDefault(b => b.Replaces == source.Id);
            if (target?.Status == "building") Assert.Equal(beforeResidents, source.Residents);
            if (target?.MoveFinished == false) Assert.True(beforeResidents - source.Residents <= 3);
            if (source.Status == "demolished") break;
        }
        Assert.NotNull(target); Assert.Equal("active", target.Status); Assert.Equal(true, target.MoveFinished);
        Assert.Equal("demolished", source.Status); Assert.Equal(0, source.Residents); Assert.True(target.Residents > 0);
        Assert.True(source.Lifecycle.DemolitionDone >= 24);
        Assert.Contains(sim.World.Journal, e => e.Type == "settlement_building_demolished" && e.SubjectId == source.Id);
        Assert.Equal(population, sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population);
        var restored = await CreateLifecycle(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(12); restored.Advance(12); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task LifecycleDemolitionSalvagesOnlyPaidMaterialsOnceAndSurvivesPartialRestore()
    {
        var sim = await CreateLifecycle(edit: r => r with { MaintenanceLaborShare = .001 });
        var b = sim.Development!.State.Buildings.Last(); b.Status = "abandoned"; b.Residents = 0;
        b.Lifecycle!.InvestedMaterials["timber"] = .12; b.Lifecycle.PermanentWear = .5;
        sim.Development.RehousePopulation(); sim.Advance(1);
        Assert.Equal("demolishing", b.Status); Assert.InRange(b.Lifecycle.DemolitionDone, .0001, 23);
        var restored = await CreateLifecycle(WorldSnapshot.Create(sim.World), edit: r => r with { MaintenanceLaborShare = .001 });
        sim.Advance(2); restored.Advance(2); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        var total = 0d;
        for (var i = 0; i < 120; i++)
        {
            sim.Advance(1); total += sim.Development.State.Cities[b.CityId].Maintenance!.Salvaged.GetValueOrDefault("timber");
        }
        Assert.Equal("demolished", b.Status); Assert.Equal(.012, total, 8);
    }

    [Fact]
    public async Task LifecycleRepairCannotCreateMissingTimberEvenWithAvailableWorkers()
    {
        var sim = await CreateLifecycle(); var state = sim.Development!.State;
        foreach (var t in sim.World.Spatial.Territories.Values) sim.Development.Extract(t, "timber", double.MaxValue);
        foreach (var city in sim.World.Cities.Values) city.Stocks["timber"] = 0;
        var b = state.Buildings.First(); b.Lifecycle!.RepairableWear = .15; b.Lifecycle.PermanentWear = .05;
        // Direct daily work isolates material availability from the separate natural regeneration step.
        sim.Development.RunDay(new DailyTelemetry { Day = sim.World.Day });
        Assert.Equal(.15, b.Lifecycle.RepairableWear); Assert.Equal(.05, b.Lifecycle.PermanentWear);
        Assert.All(state.Cities.Values, life => { Assert.True(life.LaborAvailableHours > 0); Assert.Equal(0, life.Maintenance!.RepairHours); });
    }

    [Fact]
    public async Task LifecycleStoneRequiresKnowledgeAndExtractedMaterialsBeforeConstruction()
    {
        static SettlementLifecycleRules CostlyTimber(SettlementLifecycleRules rules) => rules with
        { Materials = rules.Materials.Select(m => m.Id != "stone" ? m with { LaborMultiplier = 10000 } : m).ToArray() };
        var sim = await CreateLifecycle(edit: CostlyTimber); var state = sim.Development!.State;
        Assert.DoesNotContain(state.Cities.Values, life => life.Discoveries.Contains("masonry"));
        var city = sim.World.Cities["river_hearth"];
        foreach (var life in state.Cities.Values) life.Discoveries.Add("masonry");
        var old = state.Buildings.First(b => b.CityId == city.Id); old.Status = "abandoned"; old.Residents = 0;
        city.Stocks["stone"] = 0; sim.Development.RehousePopulation();
        sim.Advance(1); Assert.DoesNotContain(state.Buildings, b => b.Lifecycle?.Material == "stone");
        sim.Advance(90);
        var building = state.Buildings.First(b => b.CityId == city.Id && b.Lifecycle?.Material == "stone");
        Assert.Equal(.5, building.Lifecycle!.InvestedMaterials["stone"]);
        Assert.True(sim.World.Spatial.Territories.Values.Sum(t => t.NaturalState.ExtractedBatches.GetValueOrDefault("stone")) >= .5);
        Assert.True(building.RequiredLaborHours >= 400);
        Assert.True(sim.World.Telemetry.Daily.Sum(d => d.InfrastructureConsumptionByResource.GetValueOrDefault("stone")) >= .5);
    }

    [Fact]
    public async Task LifecycleWellConservesStorageAcrossHouseholdsRechargeAndSnapshot()
    {
        var sim = await CreateLifecycle(); foreach (var life in sim.Development!.State.Cities.Values) life.Discoveries.Add("well");
        sim.Advance(30);
        var wells = sim.Development.State.Buildings.Where(b => b.Kind == "well" && b.Status == "active").ToArray(); Assert.NotEmpty(wells);
        foreach (var well in wells) well.Well!.Stock = .001;
        var before = wells.ToDictionary(b => b.Id, b => b.Well!.Stock);
        sim.Advance(1);
        foreach (var b in wells)
        {
            var w = b.Well!;
            Assert.Equal(before[b.Id] + w.RechargedToday - w.OverflowToday - w.WithdrawnToday, w.Stock, 8);
            Assert.InRange(w.Stock, -1e-12, w.Capacity + 1e-12);
            Assert.InRange(w.WithdrawnToday, -1e-12, before[b.Id] + w.RechargedToday + 1e-12);
        }
        Assert.All(sim.Development.State.Cities.Values, life => Assert.True(life.WaterCoverage > .99));
        Assert.True(wells.Sum(b => b.Well!.WithdrawnToday) > 0);
        var restored = await CreateLifecycle(WorldSnapshot.Create(sim.World));
        sim.Advance(8); restored.Advance(8); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task LifecycleExhaustedFieldIsAbandonedThenRecoversWithoutResetOrInstantReplanting()
    {
        var sim = await CreateLifecycle(); sim.Advance(180); var state = sim.Development!.State;
        var plot = state.Buildings.First(b => b.Kind == "garden" && b.Status == "active" && b.ReadyDay < sim.World.Day);
        var soil = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(plot.Cell)].NaturalState;
        var before = soil.SoilQuality; var harvest = plot.Field!.Harvested;
        sim.Advance(1);
        Assert.True(plot.Field.Harvested > harvest);
        Assert.Equal(before - (plot.Field.Harvested - harvest) * sim.Development.Rules.Lifecycle!.SoilLossPerTonne, soil.SoilQuality, 9);
        soil.SoilQuality = .1; sim.Advance(15);
        Assert.Equal("abandoned", plot.Status); Assert.NotNull(plot.Field.FallowSinceDay);
        var poor = soil.SoilQuality;
        sim.Advance(60);
        Assert.True(soil.SoilQuality > poor); Assert.True(soil.SoilQuality < before);
        Assert.DoesNotContain(state.Buildings, b => b.Id != plot.Id && b.Kind == "garden" && b.Cell == plot.Cell && b.Status is "active" or "building");
        var restored = await CreateLifecycle(WorldSnapshot.Create(sim.World));
        sim.Advance(10); restored.Advance(10); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task ChangedLifecycleRulesRejectAnOldWorld()
    {
        var old = await CreateLifecycle(legacy: true); old.Advance(5); old.World.Day = 5000;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateLifecycle(WorldSnapshot.Create(old.World)));
    }

    [Fact]
    public async Task LifecycleFourYearsKeepFiniteBudgetsAndProduceMaintenanceAndFieldRotation()
    {
        var sim = await CreateLifecycle(); var repairs = 0d; var state = sim.Development!.State;
        for (var day = 0; day < 1460; day++)
        {
            sim.Advance(1); repairs += state.Cities.Values.Sum(l => l.Maintenance!.RepairHours);
            foreach (var city in sim.World.Cities.Values)
            {
                var life = state.Cities[city.Id];
                Assert.InRange(life.LaborUsedHours + life.IndustryLaborHours, 0, life.LaborAvailableHours + 1e-5);
                Assert.All(city.Stocks.Values, n => Assert.True(double.IsFinite(n) && n >= -1e-8));
                Assert.Equal(sim.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population,
                    state.Buildings.Where(b => b.CityId == city.Id).Sum(b => b.Residents) + life.Unhoused);
            }
            if ((day + 1) % 365 == 0) output.WriteLine($"year={(day + 1) / 365}: pop={state.Buildings.Sum(b => b.Residents)}, repairs={repairs:F1}h, fallow={state.Buildings.Count(b => b.Field?.FallowSinceDay != null)}, houses={state.Buildings.Count(b => b.Kind == "house" && b.Status == "active")}");
        }
        Assert.True(repairs > 0); Assert.Contains(state.Buildings, b => b.Field?.FallowSinceDay != null);
        Assert.All(sim.World.Cities.Values, city => Assert.False(city.Shortage.Active));
        Assert.True(sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population >= 470);
    }
}
