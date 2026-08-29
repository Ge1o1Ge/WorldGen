using WorldGen.Core.Simulation;

namespace WorldGen.Tests;

public sealed class SettlementStorageTests
{
    [Fact]
    public async Task StorageBuildingsUseTheSamePhysicalLifecycleAsHomes()
    {
        var simulation = await PrimitiveWorldTests.Create();
        var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First();
        var existing = development.State.Buildings.First(building => building.CityId == city.Id);
        var granary = new DwellingState { Id = "lifecycle-granary", CityId = city.Id, Kind = "granary", Cell = existing.Cell, Slot = existing.Slot, Status = "active" };
        var initialize = typeof(SettlementSimulation).GetMethod("InitializeLifecycle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        initialize.Invoke(development, [granary, false, development.Rules.Lifecycle!.Materials.Single(material => material.Id == "wood")]);

        Assert.NotNull(granary.Lifecycle);
        Assert.Equal("wood", granary.Lifecycle.Material);
    }

    [Fact]
    public async Task OrdinaryBuildingsHaveFiniteCapacityAndOutdoorOverflowDecaysFaster()
    {
        var simulation = await PrimitiveWorldTests.Create(); var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First();
        foreach (var id in city.Stocks.Keys.ToArray()) city.Stocks[id] = 0;
        var expectedCapacity = development.State.Buildings.Where(b => b.CityId == city.Id && b.Status == "active")
            .Sum(b => b.Kind == "garden" ? .08 : development.Rules.Buildings.Single(rule => rule.Id == b.Kind).StorageCapacity * (b.Lifecycle?.Efficiency ?? 1));
        city.Stocks["food"] = expectedCapacity + 1;

        var telemetry = new DailyTelemetry { Day = simulation.World.Day };
        development.DecayStoredResources(telemetry);
        var state = development.State.Cities[city.Id].Storage!;
        var decay = simulation.Content.Resources.Resources.Single(r => r.Id == "food").DecayPerDay;
        var expectedLoss = expectedCapacity * decay * development.Rules.Storage!.GeneralBuildingDecayMultiplier +
            decay * development.Rules.Storage.OutdoorDecayMultiplier;

        Assert.Equal(expectedCapacity, state.TotalCapacity, 8);
        Assert.Equal(expectedCapacity, state.StoredByResource["food"], 8);
        Assert.Equal(1, state.OutdoorByResource["food"], 8);
        Assert.Equal(expectedLoss, state.LostByResource["food"], 8);
        Assert.Equal(expectedCapacity + 1 - expectedLoss, city.Stocks["food"], 8);
        Assert.True(development.State.Cities[city.Id].PracticeHours["storage"] > 1);
    }

    [Fact]
    public async Task GranaryProtectsPreferredFoodButCannotHideOverflow()
    {
        var simulation = await PrimitiveWorldTests.Create(); var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First(); var existing = development.State.Buildings.First(b => b.CityId == city.Id);
        foreach (var building in development.State.Buildings.Where(b => b.CityId == city.Id)) building.Status = "abandoned";
        development.State.Buildings.Add(new DwellingState
        {
            Id = "test-granary", CityId = city.Id, Kind = "granary", Cell = existing.Cell, Slot = existing.Slot, Status = "active"
        });
        foreach (var id in city.Stocks.Keys.ToArray()) city.Stocks[id] = 0;
        city.Stocks["food"] = 12;

        development.DecayStoredResources(new DailyTelemetry { Day = simulation.World.Day });
        var state = development.State.Cities[city.Id].Storage!;
        var decay = simulation.Content.Resources.Resources.Single(r => r.Id == "food").DecayPerDay;
        var expectedLoss = 10 * decay * .18 + 2 * decay * development.Rules.Storage!.OutdoorDecayMultiplier;

        Assert.Equal(10, state.TotalCapacity, 8);
        Assert.Equal(10, state.StoredByResource["food"], 8);
        Assert.Equal(10, state.SpecializedByResource["food"], 8);
        Assert.Equal(2, state.OutdoorByResource["food"], 8);
        Assert.Equal(expectedLoss, state.LostToday, 8);
        Assert.True(expectedLoss < 12 * decay * development.Rules.Storage.OutdoorDecayMultiplier);
        Assert.True(development.State.Cities[city.Id].PracticeHours["food_storage"] > 2);
    }

    [Fact]
    public async Task KnownStorageTechnologiesTurnObservedOverflowIntoBuildingIdeas()
    {
        var simulation = await PrimitiveWorldTests.Create(); var development = simulation.Development!;
        var city = simulation.World.Cities.Values.First(); var life = development.State.Cities[city.Id];
        life.Discoveries.Add("storage_buildings"); life.Discoveries.Add("granary");
        life.Storage = new SettlementStorageState
        {
            OutdoorByResource = new(StringComparer.Ordinal) { ["timber"] = 1, ["food"] = 1 }
        };
        var homes = development.State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house").ToArray();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var result = (System.Collections.IEnumerable)typeof(SettlementSimulation).GetMethod("BuildingIdeas", flags)!.Invoke(development, [city, homes])!;
        var kinds = result.Cast<object>().Select(item => (string)item.GetType().GetProperty("Kind")!.GetValue(item)!).ToArray();

        Assert.Contains("warehouse", kinds);
        Assert.Contains("granary", kinds);
    }
}
