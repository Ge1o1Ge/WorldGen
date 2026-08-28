using WorldGen.Core.Topology;
using WorldGen.Core.Travel;

namespace WorldGen.Tests;

public sealed class TrailNetworkTests
{
    [Fact]
    public void PathfinderUsesClosedSphereInsteadOfTreatingFaceEdgeAsWall()
    {
        var topology = new CubeSphereTopology(16);
        var start = new CellAddress(CubeFace.PositiveX, 15, 8);
        var destination = topology.GetNeighbor(start, CardinalDirection.East);

        var path = CellPathfinder.FindPath(topology, start, destination, _ => 1);

        Assert.NotNull(path);
        Assert.Equal([start, destination], path.Cells);
        Assert.Equal(1, path.TotalCost);
    }

    [Fact]
    public void RepeatedTrafficMakesAPathCheaperAndDisuseFadesIt()
    {
        var topology = new CubeSphereTopology(12);
        var start = new CellAddress(CubeFace.PositiveZ, 3, 6);
        var destination = new CellAddress(CubeFace.PositiveZ, 9, 6);
        var trails = new TrailField(trafficForStrongTrail: 100, halfLifeDays: 100);
        var initial = CellPathfinder.FindPath(topology, start, destination, cell => trails.EffectiveTraversalCost(cell, 1));
        Assert.NotNull(initial);

        trails.RecordPassage(initial.Cells, 300);
        var established = CellPathfinder.FindPath(topology, start, destination, cell => trails.EffectiveTraversalCost(cell, 1));

        Assert.NotNull(established);
        Assert.True(established.TotalCost < initial.TotalCost * 0.5f);
        var strong = trails.GetStrength(initial.Cells[2]);
        trails.Decay(100);
        Assert.InRange(trails.GetStrength(initial.Cells[2]), strong * 0.49f, strong * 0.51f);
    }

    [Fact]
    public void PathfinderAvoidsImpassableCells()
    {
        var topology = new CubeSphereTopology(10);
        var start = new CellAddress(CubeFace.PositiveZ, 2, 5);
        var blocked = new CellAddress(CubeFace.PositiveZ, 3, 5);
        var destination = new CellAddress(CubeFace.PositiveZ, 4, 5);

        var path = CellPathfinder.FindPath(topology, start, destination, cell => cell == blocked ? float.PositiveInfinity : 1);

        Assert.NotNull(path);
        Assert.DoesNotContain(blocked, path.Cells);
        Assert.True(path.Cells.Count > 3);
    }
}
