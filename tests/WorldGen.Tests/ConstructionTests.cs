using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class ConstructionTests
{
    [Fact]
    public void CellStartsWithFourCapacityUnitsAndBuildingCanSpanSeam()
    {
        var topology = new CubeSphereTopology(12);
        var registry = new ConstructionRegistry(topology);
        var first = new CellAddress(CubeFace.PositiveX, 11, 6);
        var second = topology.GetNeighbor(first, CardinalDirection.East);
        var mill = new BuildingPlacement("mill", "city:a", "water_mill",
            [new CellCapacityAllocation(first, 4), new CellCapacityAllocation(second, 4)], 0.8f);

        registry.Place(mill);

        Assert.Equal(4, registry.GetCapacity(first));
        Assert.Equal(4, registry.GetOccupiedCapacity(first));
        Assert.Equal(4, registry.GetOccupiedCapacity(second));
        Assert.Equal(2, registry.ToInfluenceSources().Count());
    }

    [Fact]
    public void TechnologyRaisesDensityWithoutChangingCellFootprint()
    {
        var topology = new CubeSphereTopology(8);
        var registry = new ConstructionRegistry(topology);
        var cell = new CellAddress(CubeFace.PositiveZ, 4, 4);
        registry.SetTechnologyCapacityBonus(cell, 4);
        registry.Place(new BuildingPlacement("tenement", "city:a", "dense_house",
            [new CellCapacityAllocation(cell, 8)], 0.7f));

        Assert.Single(registry.Buildings);
        Assert.Equal(8, registry.GetCapacity(cell));
        Assert.Throws<InvalidOperationException>(() => registry.SetTechnologyCapacityBonus(cell, 0));
    }

    [Fact]
    public void DisconnectedMultiCellBuildingIsRejected()
    {
        var topology = new CubeSphereTopology(8);
        var registry = new ConstructionRegistry(topology);

        Assert.Throws<ArgumentException>(() => registry.Place(new BuildingPlacement("wall", "city:a", "wall",
            [
                new CellCapacityAllocation(new CellAddress(CubeFace.PositiveZ, 1, 1), 1),
                new CellCapacityAllocation(new CellAddress(CubeFace.PositiveZ, 6, 6), 1)
            ], 0.2f)));
    }
}
