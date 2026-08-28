using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    private static async Task<SphericalSimulation> CreateCouncil(JsonObject? snapshot = null, bool legacy = false, bool expensive = false)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        rules = rules with { Wellbeing = null, Lifecycle = null }; // Council accounting is independent of upkeep rules.
        if (legacy) rules = rules with { Decisions = null };
        if (expensive) rules = rules with { Buildings = rules.Buildings.Select(b => b with { Materials = new Dictionary<string, double> { ["timber"] = 1e9 } }).ToArray() };
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        return SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro,
            SphericalSettlementLayer.Build(definition, topology, terrain), rules, snapshot);
    }

    [Fact]
    public async Task ConstructionRequiresARealMandateAndReputationWaitsForUse()
    {
        var sim = await CreateCouncil(); var state = sim.Development!.State;
        foreach (var life in state.Cities.Values) life.Discoveries.Add("well");
        var initialBuildings = state.Buildings.Select(b => b.Id).ToHashSet();
        for (var day = 0; day < 90; day++)
        {
            var population = sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population;
            sim.Advance(1);
            Assert.Equal(population, state.Cities.Values.Sum(c => c.Council!.IssuedToday), 6);
            foreach (var life in state.Cities.Values)
            {
                var council = life.Council!;
                Assert.InRange(council.SpentToday, 0, council.IssuedToday + 1e-6);
                foreach (var proposal in council.Proposals.Where(p => p.BuildingId is not null))
                {
                    Assert.NotNull(proposal.ApprovedDay); Assert.True(proposal.StartedDay >= proposal.ApprovedDay);
                    Assert.NotNull(proposal.SelectedSite);
                    var started = state.Buildings.Single(b => b.Id == proposal.BuildingId);
                    var evt = sim.World.Journal.Single(e => e.Id == started.CauseEventId);
                    Assert.Contains(proposal.CauseEventId!, evt.CauseIds);
                    if (proposal.AssessedDay is not null)
                    {
                        Assert.True(proposal.AssessedDay - proposal.FinishedDay >= sim.Development.Rules.Decisions!.EvaluationDays);
                        Assert.True(proposal.ObservedDays >= sim.Development.Rules.Decisions.EvaluationDays);
                    }
                }
                Assert.True(life.LaborUsedHours <= life.LaborAvailableHours + 1e-5);
            }
            if (day < 20) Assert.All(state.Cities.Values.SelectMany(c => c.Council!.Profiles.Values), p => Assert.Empty(p.Reputation));
        }
        Assert.Contains(state.Buildings, b => !initialBuildings.Contains(b.Id) && b.Status == "active");
        Assert.Contains(state.Cities.Values.SelectMany(c => c.Council!.Proposals), p => p.Phase == "assessed");
        Assert.True(sim.World.Telemetry.Daily.Sum(d => d.InfrastructureConsumptionByResource.GetValueOrDefault("timber")) > 0);
    }

    [Fact]
    public async Task ApprovedDecisionCannotCreateMissingMaterialsOrPunishUnfinishedWork()
    {
        var sim = await CreateCouncil(expensive: true); var state = sim.Development!.State;
        foreach (var life in state.Cities.Values) life.Discoveries.Add("well");
        var initialCount = state.Buildings.Count;
        sim.Advance(20);
        Assert.Equal(initialCount, state.Buildings.Count);
        Assert.Contains(state.Cities.Values.SelectMany(c => c.Council!.Proposals), p => p.Phase == "approved");
        Assert.All(state.Cities.Values.SelectMany(c => c.Council!.Profiles.Values), p => Assert.Empty(p.Reputation));
        Assert.Equal(0, sim.World.Telemetry.Daily.Sum(d => d.InfrastructureConsumptionByResource.GetValueOrDefault("timber")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public async Task CouncilSnapshotPreservesDiscussionAndItsContinuation(int day)
    {
        var sim = await CreateCouncil();
        foreach (var life in sim.Development!.State.Cities.Values) life.Discoveries.Add("well");
        sim.Advance(day);
        Assert.Contains(sim.Development.State.Cities.Values, c => c.Council!.Proposals.Count > 0);
        var restored = await CreateCouncil(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(45); restored.Advance(45);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task OldWorldAddsAnEmptyCouncilWithoutChangingItsPeopleOrStocks()
    {
        var old = await CreateCouncil(legacy: true); old.Advance(8);
        var snapshot = WorldSnapshot.Create(old.World); var originalHash = WorldSnapshot.Hash(old.World);
        var upgraded = await CreateCouncil(snapshot);
        Assert.Equal(old.World.Day, upgraded.World.Day);
        Assert.Equal(old.World.Spatial.Nodes[old.World.Spatial.RegionNodeId].Aggregate.Population,
            upgraded.World.Spatial.Nodes[upgraded.World.Spatial.RegionNodeId].Aggregate.Population);
        foreach (var city in old.World.Cities.Values) Assert.Equal(city.Stocks.ToDictionary(), upgraded.World.Cities[city.Id].Stocks.ToDictionary());
        Assert.All(upgraded.Development!.State.Cities.Values, c => Assert.Empty(c.Council!.Proposals));
        Assert.Equal(originalHash, WorldSnapshot.Hash(old.World));
        upgraded.Advance(1);
    }
}
