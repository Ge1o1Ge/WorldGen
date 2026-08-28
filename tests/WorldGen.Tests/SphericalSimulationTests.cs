using WorldGen.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SphericalSimulationTests
{
    private static async Task<(SphericalSimulation Simulation, SphericalSettlementLayer Settlements)> Create()
    {
        var content = await ContentLoader.LoadAsync();
        var definition = await SphericalWorldLoader.LoadAsync();
        var economy = await SphericalEconomyLoader.LoadAsync();
        var topology = new CubeSphereTopology(definition.FaceSize);
        var terrain = new SphericalTerrainGenerator(definition);
        var hydro = SphericalHydrology.Build(definition, terrain);
        var settlements = SphericalSettlementLayer.Build(definition, topology, terrain);
        return (SphericalSimulation.Create(content, definition, economy, topology, terrain, hydro, settlements), settlements);
    }

    [Fact]
    public async Task ProductionAndConsumptionRunOnSparseSphericalTerrainPastDay86()
    {
        var (simulation, _) = await Create();
        Assert.Equal(3, simulation.World.Cities.Count);
        Assert.Equal(6000, simulation.World.Spatial.Nodes[simulation.World.Spatial.RegionNodeId].Aggregate.Population);
        Assert.Equal(15, simulation.World.Cities.Values.SelectMany(city => city.Industries).Select(industry => industry.RecipeId).Distinct().Count());
        Assert.InRange(simulation.World.Spatial.Territories.Count, 100, 20_000);
        foreach (var site in simulation.Sites.Values)
            Assert.Equal(site.CityId, simulation.World.Spatial.Territories[SphericalSimulation.ZoneId(site.Cell)].AssignedCityId);
        var forest = simulation.World.Spatial.Territories.ToDictionary(pair => pair.Key, pair => pair.Value.NaturalState.ForestBiomass);
        simulation.Advance(120);
        Assert.Equal(120, simulation.World.Day);
        Assert.True(simulation.World.Telemetry.Daily.Sum(day => day.HouseholdFoodConsumed) > 0);
        Assert.True(simulation.World.Telemetry.Daily.Sum(day => day.ProductionByResource.Values.Sum()) > 0);
        Assert.Contains(simulation.World.Spatial.Territories, pair => pair.Value.NaturalState.ForestBiomass < forest[pair.Key]);
        Assert.All(simulation.World.Cities.Values.SelectMany(city => city.Stocks.Values), stock => Assert.True(double.IsFinite(stock) && stock >= 0));
    }

    [Fact]
    public async Task AbandonedFieldStopsItsProductionAndIndependentRunsAreDeterministic()
    {
        var (first, settlements) = await Create();
        var (second, secondSettlements) = await Create();
        var field = first.Sites.Values.First(site => site.LandId is not null);
        settlements.SetLandUsage(field.LandId!, 0);
        secondSettlements.SetLandUsage(field.LandId!, 0);
        first.Advance(7); second.Advance(7);
        Assert.Equal(0, first.World.Cities[field.CityId].Industries.Single(industry => industry.Id == field.IndustryId).TotalBatches);
        Assert.Equal(WorldSnapshot.Hash(first.World), WorldSnapshot.Hash(second.World));
        settlements.SetLandUsage(field.LandId!, 1);
        first.Advance(7);
        Assert.True(first.World.Cities[field.CityId].Industries.Single(industry => industry.Id == field.IndustryId).TotalBatches > 0);
    }
}
