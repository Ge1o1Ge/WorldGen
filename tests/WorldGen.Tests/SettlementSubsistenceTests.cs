using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    private static async Task<SphericalSimulation> CreateFood(JsonObject? snapshot = null, bool legacy = false, bool preventGardening = false)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        rules = rules with { Wellbeing = null, Lifecycle = null }; // Isolate the earlier food-pressure contract.
        if (legacy) rules = rules with { Subsistence = null };
        if (preventGardening) rules = rules with { Discoveries = rules.Discoveries.Select(d => d.Id == "gardening" ? d with { PracticeHours = 1e12 } : d).ToArray() };
        var topology = new CubeSphereTopology(definition.FaceSize); var terrain = new SphericalTerrainGenerator(definition);
        return SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro,
            SphericalSettlementLayer.Build(definition, topology, terrain), rules, snapshot);
    }

    [Fact]
    public async Task FoodPressureAndPaidGardensRemainViableOverThreeYears()
    {
        var sim = await CreateFood(); var state = sim.Development!.State;
        foreach (var city in sim.World.Cities.Values)
        {
            var sites = sim.World.Spatial.Territories.Values.Where(t => t.AssignedCityId == city.Id && t.Terrain == "land").ToArray();
            output.WriteLine($"{city.Id}: land={sites.Length}, fertile-open={sites.Count(t => t.Fertility >= .35 && t.ForestCover < .5)}, fertility={sites.Average(t => t.Fertility):F2}, forest={sites.Average(t => t.ForestCover):F2}");
        }
        var earlyRate = 0d; var earlyDistance = 0d; var farthest = 0d; var minimumRate = double.MaxValue;
        for (var day = 0; day < 1095; day++)
        {
            sim.Advance(1);
            foreach (var city in sim.World.Cities.Values)
            {
                var life = state.Cities[city.Id]; var food = life.Food!;
                Assert.True(life.LaborUsedHours + life.IndustryLaborHours <= life.LaborAvailableHours + 1e-5);
                Assert.All(city.Stocks.Values, amount => Assert.True(double.IsFinite(amount) && amount >= -1e-8));
                Assert.InRange(life.Council!.SpentToday, 0, life.Council.IssuedToday + 1e-6);
                Assert.InRange(food.GardenOutput, 0, state.Buildings.Where(b => b.CityId == city.Id && b.Kind == "garden" && b.Status == "active" && b.ReadyDay < sim.World.Day)
                    .Sum(b => sim.Development.Rules.Subsistence!.GardenDailyYield * sim.World.Spatial.Territories[SphericalSimulation.ZoneId(b.Cell)].NaturalState.SoilQuality) + 1e-8);
            }
            var river = state.Cities["river_hearth"].Food!;
            if (day == 10) { earlyRate = river.WildOutput / river.WildHours; earlyDistance = river.MeanOneWayMeters; }
            if (day > 10) { minimumRate = Math.Min(minimumRate, river.WildOutput / Math.Max(1e-9, river.WildHours)); farthest = Math.Max(farthest, river.MeanOneWayMeters); }
            if ((day + 1) % 90 == 0 || day == 1094)
                foreach (var city in sim.World.Cities.Values)
                {
                    var life = state.Cities[city.Id]; var food = life.Food!;
                    output.WriteLine($"d{day + 1} {city.Id}: pop={sim.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population}, reserve={city.Stocks["food"] / (sim.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population * city.FoodPerPersonPerDay):F1}d, foodLabor={food.LaborHours:F0}, wild={food.WildOutput * 1000:F1}kg/{food.WildHours:F0}h, distance={food.MeanOneWayMeters:F0}, garden={food.GardenOutput * 1000:F1}kg/{food.ReadyGardens}+{food.PreparingGardens}, shortage={city.Shortage.Days}");
                }
        }
        Assert.True(minimumRate < earlyRate * .8, "Остаток должен влиять на скорость добычи");
        Assert.True(farthest > earlyDistance * 1.3, "Истощение должно увеличивать дальность поиска");
        Assert.Contains(state.Buildings, b => b.Kind == "garden" && b.Status == "active");
        Assert.All(sim.World.Cities.Values, city => Assert.False(city.Shortage.Active, city.Id));
        Assert.True(sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population >= 470);
    }

    [Fact]
    public async Task ChangedSubsistenceRulesRejectAnOldWorld()
    {
        var old = await CreateFood(legacy: true); old.Advance(3);
        var territory = old.World.Spatial.Territories.Values.First(t => old.Development!.Capacity(t, "forage") > .01);
        old.Development!.Extract(territory, "forage", old.Development.Stock(territory, "forage") * .8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateFood(WorldSnapshot.Create(old.World)));
    }

    [Fact]
    public async Task ReconciliationNeverRedistributesExistingResidentsByListOrder()
    {
        var sim = await CreateFood(); var state = sim.Development!.State;
        var before = state.Buildings.ToDictionary(b => b.Id, b => b.Residents);
        state.Buildings.Reverse(); sim.Development.RehousePopulation();
        Assert.All(state.Buildings, b => Assert.Equal(before[b.Id], b.Residents));
    }

    [Fact]
    public async Task PartialMovePaysLaborKeepsOldHouseAndSplitsInfluenceWithoutDuplication()
    {
        var sim = await CreateFood(); sim.Advance(1);
        var state = sim.Development!.State;
        var source = state.Buildings.First(b => b.Kind == "house" && b.Residents == 25);
        var city = sim.World.Cities[source.CityId]; var life = state.Cities[city.Id];
        var cell = sim.Addresses.Values.First(c => sim.World.Spatial.Territories[SphericalSimulation.ZoneId(c)].AssignedCityId == city.Id &&
            sim.World.Spatial.Territories[SphericalSimulation.ZoneId(c)].Terrain == "land" && !state.Buildings.Any(b => b.Cell == c));
        var target = new DwellingState { Id = "test-moving-house", HouseholdId = "test-moving-house", CityId = city.Id,
            Kind = "house", Cell = cell, Status = "active", Replaces = source.Id, MoveFinished = false, LaborDone = 80 };
        state.Buildings.Add(target);
        var profile = life.Council!.Profiles[source.HouseholdId!];
        profile.PracticeHours["memory"] = 250; profile.Reputation["memory"] = 1.3;
        life.Council.Proposals.Add(new CollectiveProposal { Id = "remembered-decision", Key = "remembered", Scope = city.Id,
            Domain = "memory", Kind = "house", Reason = "Historical support", Phase = "assessed", RequiredSupport = 100, RequiredSiteSupport = 10,
            Backers = [new() { SourceId = source.HouseholdId!, DeciderId = source.HouseholdId!, Points = 100 }] });
        sim.Development.RehousePopulation();
        sim = await CreateFood(WorldSnapshot.Create(sim.World)); state = sim.Development!.State;
        source = state.Buildings.Single(b => b.Id == source.Id); target = state.Buildings.Single(b => b.Id == target.Id);
        var population = sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population;
        sim.Advance(1); life = state.Cities[city.Id];
        Assert.InRange(target.Residents, 1, 3); Assert.Equal(25, source.Residents + target.Residents);
        Assert.Equal("active", source.Status); Assert.Equal(false, target.MoveFinished);
        Assert.Equal(target.Residents, life.Food!.MovedToday);
        Assert.True(life.Tasks.Where(t => t.Activity == "move").Sum(t => t.Hours) >= target.Residents * sim.Development.Rules.Subsistence!.MovingHoursPerPerson);
        Assert.Equal(population, state.Cities.Values.Sum(c => c.Council!.IssuedToday), 8);
        Assert.Equal(250, life.Council!.Profiles.Values.Sum(p => p.PracticeHours.GetValueOrDefault("memory")), 8);
        Assert.Equal(100, life.Council.Proposals.Single(p => p.Id == "remembered-decision").Support, 8);
        Assert.Equal(1.3, life.Council.Profiles[target.HouseholdId!].Reputation["memory"], 8);
        var restored = await CreateFood(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(3); restored.Advance(3);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task GardensRequireKnowledgePaidPreparationAndGrowingTimeAndResumeExactly()
    {
        var sim = await CreateFood(); var state = sim.Development!.State;
        sim.Advance(10); Assert.DoesNotContain(state.Buildings, b => b.Kind == "garden");
        for (var i = 0; i < 180 && !state.Buildings.Any(b => b.Kind == "garden" && b.Status == "active"); i++) sim.Advance(1);
        var plot = state.Buildings.First(b => b.Kind == "garden" && b.Status == "active");
        Assert.Contains("gardening", state.Cities[plot.CityId].Discoveries);
        Assert.True(plot.LaborDone >= sim.Development.Rules.Subsistence!.GardenLaborHours);
        Assert.True(plot.ReadyDay > sim.World.Day);
        Assert.DoesNotContain(state.Cities[plot.CityId].Tasks, t => t.Activity == "cultivate" && t.Destination == plot.Cell);
        Assert.Contains(state.Cities[plot.CityId].Council!.Proposals, p => p.BuildingId == plot.Id && p.ApprovedDay is not null);
        Assert.True(sim.World.Telemetry.Daily.Sum(d => d.InfrastructureConsumptionByResource.GetValueOrDefault("timber")) >= sim.Development.Rules.Subsistence.GardenTimber);
        var restored = await CreateFood(WorldSnapshot.Create(sim.World));
        sim.Advance(35); restored.Advance(35);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        Assert.True(state.Cities.Values.Sum(c => c.Food!.GardenOutput) > 0);
    }
}
