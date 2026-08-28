using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    private static async Task<SphericalSimulation> CreateScouting(bool pressure = true, JsonObject? snapshot = null, bool legacy = false)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        // Deliberately strict labor threshold exercises decisions, not a change to
        // the user's healthy scenario and not a forced expedition timer.
        // These isolate expedition accounting from the separate depletion/adaptation scenario.
        rules = rules with { Wellbeing = null, Lifecycle = null, Subsistence = null, Exploration = legacy ? null : rules.Exploration! with { LaborPressureShare = pressure ? .05 : .65 } };
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        return SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro,
            SphericalSettlementLayer.Build(definition, topology, terrain), rules, snapshot);
    }

    [Fact]
    public async Task HealthySupplyDoesNotInventExpansionAndHistoryIsBounded()
    {
        var sim = await CreateScouting(pressure: false); sim.Advance(90);
        Assert.Empty(sim.Development!.State.Scouting!.Expeditions);
        Assert.All(sim.Development.State.Cities.Values, life =>
        {
            var supply = life.Supply!;
            Assert.Equal(14, supply.History.Count);
            Assert.Equal(0, supply.PressureStreak);
            Assert.InRange(supply.LaborShare, 0, .65);
            Assert.True(supply.AccessibleCells > 0);
            Assert.Empty(supply.Reports);
        });
    }

    [Fact]
    public async Task PersistentPressurePaysForScoutsAndReportsArriveOnlyAfterPhysicalReturn()
    {
        var sim = await CreateScouting(); var state = sim.Development!.State;
        sim.Advance(7); Assert.Empty(state.Scouting!.Expeditions);
        Assert.Contains(state.Cities.Values, life => life.Supply!.PressureStreak == 7);
        Assert.All(state.Cities.Values, life => Assert.InRange(life.Supply!.PressureStreak, 0, 7));
        sim.Advance(1);
        Assert.NotEmpty(state.Scouting.Expeditions);
        Assert.All(state.Scouting.Expeditions, e =>
        {
            Assert.Equal("outbound", e.Phase); Assert.NotEqual(e.Home, e.Current);
            Assert.True(e.Food > 0 && e.Water > 0);
            Assert.NotEmpty(e.Observations); Assert.Empty(state.Cities[e.CityId].Supply!.Reports);
            Assert.Equal(e.People * sim.Development.Rules.WorkHoursPerDay, state.Cities[e.CityId].Supply!.ScoutLaborHours);
        });
        var topology = new CubeSphereTopology(sim.World.Spatial.Grid.Height);
        for (var day = 0; day < 5; day++) sim.Advance(1);
        Assert.All(state.Scouting.Expeditions, e =>
        {
            Assert.Equal("returned", e.Phase); Assert.Equal(e.Home, e.Current);
            Assert.Equal(0, e.Food); Assert.Equal(0, e.Water);
            var report = Assert.Single(state.Cities[e.CityId].Supply!.Reports);
            Assert.Equal(e.ReturnDay, report.ReceivedDay);
            Assert.True(report.ReceivedDay > report.DepartureDay);
            Assert.All(report.Candidates, candidate =>
            {
                Assert.True(candidate.ObservedDay < report.ReceivedDay);
                Assert.True(candidate.FreshWater); Assert.True(candidate.FoodRenewalPerDay > 0);
                Assert.DoesNotContain(candidate.Cell, sim.Addresses.Values);
            });
            for (var i = 1; i < e.Path.Count; i++) Assert.Contains(e.Path[i], topology.GetNeighbors(e.Path[i - 1]));
            // Knowing terrain isn't automatically meeting every city on the server.
            Assert.Single(sim.World.Cities[e.CityId].KnowledgeState.KnownSettlements!);
        });
        Assert.Contains(state.Cities.Values, c => c.Supply!.Reports.Any(r => r.SurveyedCells > 0));
        Assert.All(state.Trails, edge => Assert.Contains(edge.To, topology.GetNeighbors(edge.From)));
        Assert.Contains(sim.World.Journal, e => e.Type == "supply_pressure");
        Assert.Contains(sim.World.Journal, e => e.Type == "scouting_returned");
        foreach (var e in state.Scouting.Expeditions)
        {
            var departure = sim.World.Journal.Single(evt => evt.Id == e.CauseEventId);
            Assert.Contains(departure.CauseIds, id => sim.World.Journal.Any(evt => evt.Id == id && evt.Type == "supply_pressure"));
        }
    }

    [Fact]
    public async Task CityAndExpeditionInventoriesBalanceAndWorkersAreNotCountedTwice()
    {
        var sim = await CreateScouting(); var state = sim.Development!.State;
        double Stock(string resource) => sim.World.Cities.Values.Sum(c => c.Stocks[resource]) +
            state.Scouting!.Expeditions.Sum(e => resource == "food" ? e.Food : e.Water);
        for (var day = 0; day < 20; day++)
        {
            var before = new[] { "food", "water" }.ToDictionary(id => id, Stock);
            sim.Advance(1); var t = sim.World.Telemetry.Daily.Last();
            foreach (var id in before.Keys)
            {
                var expected = before[id] + t.ProductionByResource.GetValueOrDefault(id) - t.DecayedByResource.GetValueOrDefault(id) -
                    t.HouseholdConsumptionByResource.GetValueOrDefault(id) - t.IndustrialConsumptionByResource.GetValueOrDefault(id) -
                    t.InfrastructureConsumptionByResource.GetValueOrDefault(id);
                Assert.InRange(Math.Abs(expected - Stock(id)), 0, .00001);
            }
            Assert.All(state.Cities.Values, life =>
            {
                Assert.True(life.LaborUsedHours + life.IndustryLaborHours <= life.LaborAvailableHours + 1e-5);
                Assert.True(life.Supply!.ScoutLaborHours <= life.LaborUsedHours);
            });
            Assert.All(sim.World.Cities.Values, city => Assert.All(city.Stocks.Values, n => Assert.True(double.IsFinite(n) && n >= 0)));
        }
        Assert.NotEmpty(state.Scouting!.Expeditions);
    }

    [Theory]
    [InlineData(8, "outbound")]
    [InlineData(10, "returning")]
    public async Task MidJourneySaveContinuesIdentically(int day, string phase)
    {
        var original = await CreateScouting(); original.Advance(day);
        Assert.Contains(original.Development!.State.Scouting!.Expeditions, e => e.Phase == phase);
        var restored = await CreateScouting(snapshot: WorldSnapshot.Create(original.World));
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
        original.Advance(15); restored.Advance(15);
        Assert.Equal(WorldSnapshot.Hash(original.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task NoWorkersOrNoProvisionsPreventsDeparture()
    {
        var sim = await CreateScouting(); sim.Advance(7);
        foreach (var city in sim.World.Cities.Values.ToArray())
        {
            sim.World.Cities[city.Id] = city with { WorkerShare = 0 };
            city.Stocks["food"] = 0; city.Stocks["water"] = 0;
        }
        sim.Advance(3);
        Assert.Empty(sim.Development!.State.Scouting!.Expeditions);
        Assert.All(sim.Development.State.Cities.Values, life => Assert.Equal(0, life.Supply!.ScoutLaborHours));
    }

    [Fact]
    public async Task AvailableWorkersCannotDepartWithoutFoodAndWater()
    {
        var sim = await CreateScouting(); sim.Advance(7);
        foreach (var city in sim.World.Cities.Values) city.Stocks["water"] = 0;
        sim.Advance(1);
        Assert.Empty(sim.Development!.State.Scouting!.Expeditions);
        Assert.All(sim.Development.State.Cities.Values, life => Assert.True(life.LaborAvailableHours > 0));
    }

    [Fact]
    public async Task PreviousSupplyRulesMigrateWithoutResettingTheWorld()
    {
        var old = await CreateScouting(pressure: false, legacy: true); old.Advance(9);
        var snapshot = WorldSnapshot.Create(old.World);
        var restored = await CreateScouting(pressure: false, snapshot: snapshot);
        Assert.Equal(old.World.Day, restored.World.Day);
        foreach (var city in old.World.Cities.Values) Assert.Equal(city.Stocks.ToDictionary(), restored.World.Cities[city.Id].Stocks.ToDictionary());
        Assert.All(restored.Development!.State.Cities.Values, life => Assert.Empty(life.Supply!.History));
        restored.Advance(1);
        Assert.All(restored.Development.State.Cities.Values, life => Assert.Single(life.Supply!.History));
    }

    [Fact]
    public async Task ThreeYearsOfRepeatedScoutingHaveBoundedReportsAndPaths()
    {
        var sim = await CreateScouting();
        for (var year = 0; year < 3; year++) sim.Advance(365);
        var state = sim.Development!.State; var rules = sim.Development.Rules.Exploration!;
        Assert.Equal(3, state.Scouting!.Expeditions.Count);
        Assert.All(state.Cities.Values, life =>
        {
            Assert.Equal(rules.MaximumReports, life.Supply!.Reports.Count);
            Assert.Equal(rules.WindowDays, life.Supply.History.Count);
        });
        Assert.All(state.Scouting.Expeditions, e => Assert.InRange(e.Path.Count, 1, rules.StepsPerDay * rules.OutboundDays + 1));
        foreach (var life in state.Cities.Values)
        {
            var council = life.Council!; var decisionRules = sim.Development.Rules.Decisions!;
            Assert.InRange(council.Proposals.Count(p => p.Phase is "assessed" or "cancelled" or "uncertain"), 0, decisionRules.RetainedResults);
            Assert.All(council.Profiles.Values.SelectMany(p => p.Reputation.Values), r => Assert.InRange(r, decisionRules.ReputationMinimum, decisionRules.ReputationMaximum));
            Assert.InRange(council.SpentToday, 0, council.IssuedToday + 1e-6);
        }
        Assert.All(sim.World.Cities.Values, city => Assert.False(city.Shortage.Active));
        output.WriteLine($"day={sim.World.Day}, reports={state.Cities.Values.Sum(c => c.Supply!.Reports.Count)}, trails={state.Trails.Count}");
    }

    [Fact]
    public void ScoutingRulesRejectUnsafeBudgets()
    {
        var rules = new SettlementExplorationRules(); rules.Validate();
        Assert.Throws<InvalidOperationException>(() => (rules with { ProvisionDays = 2 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { MaximumLaborShare = .8 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { LaborPressureShare = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { PressureDays = 100 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { OutboundDays = int.MaxValue }).Validate());
    }
}
