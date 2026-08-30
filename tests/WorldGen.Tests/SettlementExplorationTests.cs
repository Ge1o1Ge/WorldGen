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
        for (var day = 0; day < 20 && state.Scouting.Expeditions.Count == 0; day++) sim.Advance(1);
        Assert.NotEmpty(state.Scouting.Expeditions);
        Assert.True(state.Scouting.Expeditions.Select(e => (Math.Round(e.Direction.X, 2), Math.Round(e.Direction.Y, 2), Math.Round(e.Direction.Z, 2))).Distinct().Count() > 1,
            "Первые советы разных поселений выбрали одно глобальное направление");
        Assert.All(state.Scouting.Expeditions, e =>
        {
            Assert.Equal("outbound", e.Phase); Assert.NotEqual(e.Home, e.Current);
            Assert.True(e.Food > 0 && e.Water > 0);
            Assert.InRange(e.ProvisionDays, 4, 14); Assert.True(e.CargoUsed <= e.CargoCapacity + 1e-9);
            Assert.NotNull(e.DecisionId);
            Assert.NotEmpty(e.Observations); Assert.Empty(state.Cities[e.CityId].Supply!.Reports);
            Assert.Equal(e.People * sim.Development.Rules.WorkHoursPerDay, state.Cities[e.CityId].Supply!.ScoutLaborHours);
        });
        var topology = new CubeSphereTopology(sim.World.Spatial.Grid.Height);
        for (var day = 0; day < 40 && state.Scouting.Expeditions.Any(e => e.Phase is "outbound" or "returning"); day++) sim.Advance(1);
        Assert.All(state.Scouting.Expeditions, e =>
        {
            Assert.Equal("returned", e.Phase); Assert.Equal(e.Home, e.Current);
            Assert.Equal(0, e.Food); Assert.Equal(0, e.Water);
            var report = Assert.Single(state.Cities[e.CityId].Supply!.Reports);
            Assert.Equal(e.ReturnDay, report.ReceivedDay);
            Assert.True(report.ReceivedDay > report.DepartureDay);
            Assert.NotNull(report.Plants); Assert.NotNull(report.Animals); Assert.NotNull(report.CapturedAnimals);
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
        var returned = sim.World.Journal.First(e => e.Type == "scouting_returned");
        Assert.True(returned.Details!["durationDays"]!.GetValue<int>() > 0);
        Assert.True(returned.Details["routeCells"]!.GetValue<int>() > 0);
        Assert.NotNull(returned.Details["territorySample"]);
        foreach (var e in state.Scouting.Expeditions)
        {
            var departure = sim.World.Journal.Single(evt => evt.Id == e.CauseEventId);
            Assert.Contains(departure.CauseIds, id => sim.World.Journal.Any(evt => evt.Id == id && evt.Type == "supply_pressure"));
        }
    }

    [Fact]
    public async Task ExpeditionTransportNeedsBothKnowledgeAndARealAnimalWhileRaftsReserveTimber()
    {
        var sim = await CreateScouting(); var development = sim.Development!;
        var city = sim.World.Cities.Values.OrderBy(city => city.Id, StringComparer.Ordinal).First();
        var life = development.State.Cities[city.Id];
        life.Discoveries.Add("draught_animals"); life.Discoveries.Add("riding");
        object Plan(string key) => typeof(SettlementSimulation).GetMethod("PlanExpedition",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(development, [city, key])!;
        static double Number(object plan, string property) => Convert.ToDouble(plan.GetType().GetProperty(property)!.GetValue(plan));
        static bool Flag(object plan, string property) => (bool)plan.GetType().GetProperty(property)!.GetValue(plan)!;

        var knowledgeOnly = Plan("transport-test");
        Assert.Equal(development.Rules.Exploration!.PartySize * development.Rules.Exploration.BaseCarryTonnesPerPerson,
            Number(knowledgeOnly, "Capacity"), 10);
        Assert.Equal(1, Number(knowledgeOnly, "SpeedMultiplier"));

        life.Biology ??= new();
        life.Biology.Herds["horse"] = new HerdState { Females = 1 };
        var equipped = Plan("transport-test");
        Assert.Equal(Number(knowledgeOnly, "Capacity") * development.Rules.Exploration.PackAnimalCapacityMultiplier,
            Number(equipped, "Capacity"), 10);
        Assert.Equal(development.Rules.Exploration.RidingSpeedMultiplier, Number(equipped, "SpeedMultiplier"));

        life.Discoveries.Add("rafts"); city.Stocks["timber"] = 10;
        var waterReady = Plan("transport-test");
        Assert.True(Flag(waterReady, "RaftReady"));
        Assert.Equal(development.Rules.Exploration.RaftTimberTonnes, Number(waterReady, "RaftTimber"), 10);
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
    [InlineData("outbound")]
    [InlineData("returning")]
    public async Task MidJourneySaveContinuesIdentically(string phase)
    {
        var original = await CreateScouting();
        for (var day = 0; day < 50 && !original.Development!.State.Scouting!.Expeditions.Any(e => e.Phase == phase); day++) original.Advance(1);
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
    public async Task ChangedSupplyRulesRejectAnOldWorld()
    {
        var old = await CreateScouting(pressure: false, legacy: true); old.Advance(9);
        var snapshot = WorldSnapshot.Create(old.World);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateScouting(pressure: false, snapshot: snapshot));
    }

    [Fact]
    public async Task ThreeYearsOfRepeatedScoutingHaveBoundedReportsAndPaths()
    {
        var sim = await CreateScouting();
        for (var year = 0; year < 3; year++) sim.Advance(365);
        var state = sim.Development!.State; var rules = sim.Development.Rules.Exploration!;
        Assert.Equal(3, state.Scouting!.Expeditions.Count);
        Assert.All(state.Scouting.RecentDirections.Values, directions =>
        {
            Assert.InRange(directions.Count, 2, 6);
            for (var i = 1; i < directions.Count; i++)
                Assert.True(directions[i - 1].Dot(directions[i]) < .92, "Совет повторно утвердил почти тот же сектор разведки");
        });
        foreach (var departures in sim.World.Journal.Where(e => e.Type == "scouting_departed").GroupBy(e => e.SubjectId))
        {
            var days = departures.Select(e => e.Day).Order().ToArray();
            for (var i = 1; i < days.Length; i++) Assert.True(days[i] - days[i - 1] >= rules.CooldownDays);
        }
        Assert.All(state.Cities.Values, life =>
        {
            Assert.Equal(rules.MaximumReports, life.Supply!.Reports.Count);
            Assert.Equal(rules.WindowDays, life.Supply.History.Count);
        });
        Assert.All(state.Scouting.Expeditions, e => Assert.InRange(e.Path.Count, 1,
            Math.Min(64, (int)Math.Ceiling(rules.StepsPerDay * e.SpeedMultiplier)) * (e.PlannedOutboundDays + e.ExtensionDays) + 1));
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
        Assert.Throws<InvalidOperationException>(() => (rules with { MinimumProvisionDays = 2 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { MaximumLaborShare = .8 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { LaborPressureShare = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { PressureDays = 100 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { MaximumProvisionDays = int.MaxValue }).Validate());
    }
}
