using WorldGen.Core.Simulation;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    [Fact]
    public async Task ChangedWildlifeRulesRejectAnOldWorld()
    {
        var old = await CreateFood(legacy: true); old.Advance(5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFood(WorldSnapshot.Create(old.World)));
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
    public async Task CalmWildlifeDoesNotBounceBetweenTwoCellsAndEventuallyForgetsAnOldThreat()
    {
        var sim = await CreateFood(); var development = sim.Development!;
        var topology = new CubeSphereTopology(sim.World.Spatial.Grid.Height);
        var interval = development.Rules.Subsistence!.Wildlife.QuietMoveIntervalDays;
        var selected = development.State.Wildlife!.Select((group, index) => (group, index)).First(item =>
            (sim.World.Day + item.index) % interval == 0 && topology.GetNeighbors(item.group.Center).Count(c =>
                sim.World.Spatial.Territories.TryGetValue(SphericalSimulation.ZoneId(c), out var territory) &&
                territory.Terrain == "land" && territory.NaturalState.ForestBiomass > .02) > 2);
        var group = selected.group;
        var previous = topology.GetNeighbors(group.Center).First(c =>
            sim.World.Spatial.Territories.TryGetValue(SphericalSimulation.ZoneId(c), out var territory) &&
            territory.Terrain == "land" && territory.NaturalState.ForestBiomass > .02);
        group.PreviousCenter = previous; group.Alert = 0; group.Threat = null;
        typeof(SettlementSimulation).GetMethod("AdvanceWildlife", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(development, null);
        Assert.NotEqual(previous, group.Center);

        var halfLife = development.Rules.Subsistence.Wildlife.AlertHalfLifeDays;
        while (sim.World.Day <= halfLife * 2 + 2) sim.Advance(1);
        group.Alert = development.Rules.Subsistence.Wildlife.FleeThreshold * .1;
        group.Threat = topology.GetNeighbors(group.Center)[0]; group.LastHuntedDay = 0;
        development.RecoverNaturalSites();
        Assert.Null(group.Threat);
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
