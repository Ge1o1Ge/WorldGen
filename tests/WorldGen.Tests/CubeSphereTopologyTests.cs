using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class CubeSphereTopologyTests
{
    [Fact]
    public void SurfaceHasSixFacesAndTwoTrianglesPerCell()
    {
        var topology = new CubeSphereTopology(416);

        Assert.Equal(1_038_336, topology.CellCount);
        Assert.Equal(2_076_672, topology.TriangleCount);
    }

    [Fact]
    public void EveryCellHasFourDistinctReciprocalNeighbors()
    {
        const int size = 13;
        var topology = new CubeSphereTopology(size);

        foreach (var face in Enum.GetValues<CubeFace>())
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var cell = new CellAddress(face, x, y);
                    var neighbors = topology.GetNeighbors(cell);
                    Assert.Equal(4, neighbors.Distinct().Count());
                    Assert.All(neighbors, neighbor => Assert.True(topology.Contains(neighbor)));
                    Assert.All(neighbors, neighbor => Assert.Contains(cell, topology.GetNeighbors(neighbor)));
                }
            }
        }
    }

    [Fact]
    public void CrossingEveryFaceEdgeRemainsGeometricallyLocal()
    {
        const int size = 32;
        var topology = new CubeSphereTopology(size);
        var edgeCells = Enum.GetValues<CubeFace>().SelectMany(face => Enumerable.Range(0, size).SelectMany(index => new[]
        {
            (new CellAddress(face, 0, index), CardinalDirection.West),
            (new CellAddress(face, size - 1, index), CardinalDirection.East),
            (new CellAddress(face, index, 0), CardinalDirection.North),
            (new CellAddress(face, index, size - 1), CardinalDirection.South)
        }));

        foreach (var (cell, direction) in edgeCells)
        {
            var neighbor = topology.GetNeighbor(cell, direction);
            Assert.NotEqual(cell.Face, neighbor.Face);
            var dot = topology.ToUnitVector(cell).Dot(topology.ToUnitVector(neighbor));
            Assert.True(dot > 0.995, $"Слишком большой разрыв {cell} -> {neighbor}: dot={dot}");
        }
    }

    [Fact]
    public void ChunkLayoutRoundTripsCellsAndHasNoPartialChunksForPrototype()
    {
        var layout = new ChunkLayout(416, 32);
        var cell = new CellAddress(CubeFace.NegativeZ, 415, 33);

        var located = layout.Locate(cell);

        Assert.Equal(new ChunkAddress(CubeFace.NegativeZ, 12, 1), located.Chunk);
        Assert.Equal(31, located.LocalX);
        Assert.Equal(1, located.LocalY);
        Assert.Equal((32, 32), layout.GetChunkDimensions(located.Chunk));
        Assert.Equal(1_014, layout.ChunkCount);
    }
}
