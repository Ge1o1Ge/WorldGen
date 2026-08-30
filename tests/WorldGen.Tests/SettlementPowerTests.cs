using System.Reflection;
using System.Collections;
using WorldGen.Core.Simulation;

namespace WorldGen.Tests;

public sealed class SettlementPowerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task MillingNeedsARealInstallationAndUsesItsLaborMultiplier()
    {
        var simulation = await PrimitiveWorldTests.Create();
        var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First();
        var life = development.State.Cities[city.Id];
        foreach (var resource in city.Stocks.Keys.ToArray()) city.Stocks[resource] = 0;
        life.Discoveries.Add("millstone");
        city.Stocks["harvest_wheat"] = 1;
        city.Stocks["stone_kit"] = .2;
        var run = typeof(SettlementSimulation).GetMethod("RunPrimitiveProcesses", Private)!;

        run.Invoke(development, [city, 200d, new DailyTelemetry { Day = simulation.World.Day }]);
        Assert.StartsWith("building:any:", life.Processes["mill_wheat"].Constraint);
        Assert.Equal(0, city.Stocks["flour"]);

        var home = development.State.Buildings.First(building => building.CityId == city.Id);
        development.State.Buildings.Add(new DwellingState
        {
            Id = "test-water-mill", CityId = city.Id, Kind = "water_mill", Cell = home.Cell,
            Slot = home.Slot, Status = "active"
        });
        run.Invoke(development, [city, 200d, new DailyTelemetry { Day = simulation.World.Day }]);

        var state = life.Processes["mill_wheat"];
        Assert.Equal("test-water-mill", state.BuildingId);
        Assert.Equal(.18, state.LaborMultiplier, 8);
        Assert.True(city.Stocks["flour"] > 0);
        Assert.Contains(life.Tasks, task => task.HomeId == "test-water-mill" && task.Activity == "process:mill_wheat");
    }

    [Fact]
    public async Task LifecycleShellCannotEraseSpecialisedBuildingComponents()
    {
        var simulation = await PrimitiveWorldTests.Create();
        var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First();
        var projectRule = typeof(SettlementSimulation).GetMethod("ProjectRule", Private)!;

        var rule = (SettlementBuildingRule)projectRule.Invoke(development, [city, "water_mill"])!;

        Assert.Equal(1, rule.Materials["mechanism_kit"]);
        Assert.Equal(1, rule.Materials["millstone"]);
        Assert.Contains(rule.Materials, pair => pair.Key is "timber" or "stone" or "clay");
    }

    [Fact]
    public async Task CottonBecomesClothAndThenActualGarmentsThroughConfiguredProcesses()
    {
        var simulation = await PrimitiveWorldTests.Create();
        var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First();
        var life = development.State.Cities[city.Id];
        foreach (var resource in city.Stocks.Keys.ToArray()) city.Stocks[resource] = 0;
        life.Discoveries.UnionWith(["textile_weaving", "woven_clothing"]);
        city.Stocks["harvest_cotton"] = 1;
        city.Stocks["stone_kit"] = .2;
        var run = typeof(SettlementSimulation).GetMethod("RunPrimitiveProcesses", Private)!;

        run.Invoke(development, [city, 500d, new DailyTelemetry { Day = simulation.World.Day }]);
        Assert.True(city.Stocks["woven_cloth"] > 0);
        Assert.True(city.Stocks["harvest_cotton"] < 1);
        run.Invoke(development, [city, 500d, new DailyTelemetry { Day = simulation.World.Day }]);

        Assert.True(city.Stocks["garments"] > 0);
        Assert.True(life.Processes["sew_woven_clothing"].TotalBatches > 0);
    }

    [Fact]
    public async Task KnownTechnicalCropCreatesARealExperimentalFieldProposal()
    {
        var simulation = await PrimitiveWorldTests.Create();
        var development = simulation.Development!;
        var cotton = development.Rules.Primitive!.Biosphere!.Crops.Single(crop => crop.Id == "cotton");
        var proposed = false;
        foreach (var city in simulation.World.Cities.Values)
        {
            development.State.Cities[city.Id].Discoveries.UnionWith(["gardening", "grow_cotton"]);
            city.Stocks[cotton.SeedResource] = cotton.SeedTonnes;
            var homes = development.State.Buildings.Where(building => building.CityId == city.Id && building.Kind == "house").ToArray();
            var ideas = (IEnumerable)typeof(SettlementSimulation).GetMethod("BuildingIdeas", Private)!.Invoke(development, [city, homes])!;
            proposed |= ideas.Cast<object>().Any(idea => (string)idea.GetType().GetProperty("Key")!.GetValue(idea)! == "technical-crop:cotton");
        }

        Assert.True(proposed);
    }
}
