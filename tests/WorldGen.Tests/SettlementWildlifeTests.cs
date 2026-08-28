using WorldGen.Core.Simulation;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    [Fact]
    public async Task MobileGameMigrationTransfersBiomassInsteadOfCopyingCellStocks()
    {
        var old = await CreateFood(legacy: true); old.Advance(5);
        var total = old.World.Spatial.Territories.Values.Sum(t => old.Development!.Stock(t, "game"));
        var snapshot = WorldSnapshot.Create(old.World); var original = WorldSnapshot.Hash(old.World);
        var sim = await CreateFood(snapshot); var state = sim.Development!.State;
        Assert.NotEmpty(state.Wildlife!);
        Assert.Equal(total, state.Wildlife!.Sum(g => g.Biomass), 8);
        Assert.All(state.WildStocks.Values, stocks => Assert.False(stocks.ContainsKey("game")));
        foreach (var city in old.World.Cities.Values) Assert.Equal(city.Stocks.ToDictionary(), sim.World.Cities[city.Id].Stocks.ToDictionary());
        Assert.Equal(original, WorldSnapshot.Hash(old.World));
        sim.Advance(30);
        Assert.Equal(total, state.Wildlife!.Sum(g => g.Biomass + g.Harvested - g.Regrown), 8);
        var restored = await CreateFood(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(5); restored.Advance(5);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task HuntingMovesTheAttackedZoneAwayAndNeverThroughWaterOrAcrossMultipleCells()
    {
        var sim = await CreateFood(); var development = sim.Development!;
        var topology = new CubeSphereTopology(sim.World.Spatial.Grid.Height);
        var group = development.State.Wildlife!.First(g => topology.GetNeighbors(g.Center).All(c =>
            sim.World.Spatial.Territories.TryGetValue(SphericalSimulation.ZoneId(c), out var t) && t.Terrain == "land"));
        var center = group.Center; var hunter = topology.GetNeighbors(center)[0];
        var territory = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(center)];
        var initialMass = development.State.Wildlife!.Sum(g => g.Biomass);
        var taken = development.Extract(territory, "game", .08, hunter);
        Assert.True(taken > 0); Assert.True(group.Alert >= development.Rules.Subsistence!.Wildlife.FleeThreshold);
        Assert.Equal(initialMass - taken, development.State.Wildlife!.Sum(g => g.Biomass), 8);
        development.RecoverNaturalSites();
        Assert.Contains(group.Center, topology.GetNeighbors(center));
        Assert.NotEqual(center, group.Center); Assert.Equal(center, group.PreviousCenter);
        var threat = topology.ToUnitVector(hunter);
        Assert.True(topology.ToUnitVector(group.Center).Dot(threat) < topology.ToUnitVector(center).Dot(threat));
        Assert.Equal("land", sim.World.Spatial.Territories[SphericalSimulation.ZoneId(group.Center)].Terrain);
        Assert.Equal(initialMass, development.State.Wildlife!.Sum(g => g.Biomass + g.Harvested - g.Regrown), 8);
    }

    [Fact]
    public async Task FishingRetainsLargeStockButEachCatchMakesFurtherFishingHarderAndRecoveryTakesYears()
    {
        var sim = await CreateFood(); var development = sim.Development!;
        var site = sim.World.Spatial.Territories.Values.First(t => development.Stock(t, "fish") == 12);
        var initial = development.EncounterRate(site, "fish");
        Assert.Equal(.25, initial, 8);
        development.Extract(site, "fish", 1);
        Assert.Equal(11, development.Stock(site, "fish"), 8);
        var afterFirst = development.EncounterRate(site, "fish");
        Assert.True(afterFirst < initial / 2);
        development.Extract(site, "fish", 1);
        Assert.True(development.EncounterRate(site, "fish") < afterFirst);
        var stock = development.Stock(site, "fish");
        // Isolate the stock recovery equation from daily household catches.
        var afterYear = development.Capacity(site, "fish") - (development.Capacity(site, "fish") - stock) * Math.Pow(1 - development.RecoveryRate("fish"), 365);
        Assert.InRange(afterYear, stock, 11);
    }

    [Fact]
    public async Task LandPreparationActuallyRemovesForestAndDepletionReducesWoodEfficiency()
    {
        var sim = await CreateFood(); var state = sim.Development!.State;
        for (var i = 0; i < 180 && !state.Buildings.Any(b => b.Kind == "garden" && b.Status == "active"); i++) sim.Advance(1);
        var plot = state.Buildings.First(b => b.Kind == "garden" && b.Status == "active");
        var site = sim.World.Spatial.Territories[SphericalSimulation.ZoneId(plot.Cell)];
        Assert.True(site.ForestCover > 0); Assert.InRange(site.NaturalState.ForestBiomass, 0, 1e-8);
        Assert.True(plot.RequiredLaborHours > sim.Development.Rules.Subsistence!.GardenLaborHours);
        Assert.InRange(plot.ClearingRemaining!.Value, 0, 1e-8);
        Assert.True(site.NaturalState.ExtractedBatches.GetValueOrDefault("timber") > 0);
        Assert.Equal(0, sim.Development.EncounterRate(site, "timber"), 6);
    }
}
