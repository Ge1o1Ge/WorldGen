using WorldGen.Content;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SphericalTerrainTests
{
    [Fact]
    public async Task PrototypeDefinitionIsMillionZoneChunkedWorld()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var layout = new ChunkLayout(definition.FaceSize, definition.ChunkSize);

        Assert.Equal("cube_sphere", definition.Topology);
        Assert.InRange(definition.ZoneCount, 1_000_000, 1_100_000);
        Assert.Equal(1_014, layout.ChunkCount);
        Assert.Equal(2 * definition.ZoneCount, definition.TriangleCount);
    }

    [Fact]
    public async Task GeneratedChunksAreCompactAndDeterministic()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var generator = new SphericalTerrainGenerator(definition);
        var address = new ChunkAddress(CubeFace.PositiveZ, 6, 6);

        var first = generator.GenerateChunk(address);
        var second = generator.GenerateChunk(address);

        Assert.Equal(1_024, first.CellCount);
        Assert.Equal(25_600, first.AllocatedDataBytes);
        Assert.Equal(first.ContentHash(), second.ContentHash());
        Assert.InRange(generator.EstimateResidentTerrainBytes(), 20_000_000, 30_000_000);
        var centerChunks = Enum.GetValues<CubeFace>()
            .Select(face => generator.GenerateChunk(new ChunkAddress(face, 6, 6)))
            .ToArray();
        Assert.Contains(centerChunks.SelectMany(chunk => chunk.Biome), biome => biome != SphericalBiome.Ocean);
    }

    [Fact]
    public async Task TerrainSignalDoesNotJumpAtFaceSeams()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var topology = new CubeSphereTopology(definition.FaceSize);
        var generator = new SphericalTerrainGenerator(definition);
        var layout = new ChunkLayout(definition.FaceSize, definition.ChunkSize);
        var chunks = new Dictionary<ChunkAddress, TerrainChunk>();

        foreach (var face in Enum.GetValues<CubeFace>())
        {
            foreach (var index in new[] { 0, definition.FaceSize / 2, definition.FaceSize - 1 })
            {
                foreach (var (cell, direction) in new[]
                {
                    (new CellAddress(face, 0, index), CardinalDirection.West),
                    (new CellAddress(face, definition.FaceSize - 1, index), CardinalDirection.East),
                    (new CellAddress(face, index, 0), CardinalDirection.North),
                    (new CellAddress(face, index, definition.FaceSize - 1), CardinalDirection.South)
                })
                {
                    var neighbor = topology.GetNeighbor(cell, direction);
                    var difference = Math.Abs(Elevation(cell) - Elevation(neighbor));
                    Assert.True(difference < 75, $"Разрыв рельефа {cell} -> {neighbor}: {difference:F2} м");
                }
            }
        }

        return;

        float Elevation(CellAddress cell)
        {
            var location = layout.Locate(cell);
            if (!chunks.TryGetValue(location.Chunk, out var chunk))
            {
                chunk = generator.GenerateChunk(location.Chunk);
                chunks.Add(location.Chunk, chunk);
            }
            return chunk.ElevationMeters[chunk.Index(location.LocalX, location.LocalY)];
        }
    }

    [Fact]
    public async Task DirectCellSampleMatchesItsChunkStorage()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var generator = new SphericalTerrainGenerator(definition);
        var layout = new ChunkLayout(definition.FaceSize, definition.ChunkSize);
        var cell = new CellAddress(CubeFace.NegativeY, 173, 299);
        var location = layout.Locate(cell);
        var chunk = generator.GenerateChunk(location.Chunk);
        var index = chunk.Index(location.LocalX, location.LocalY);

        var sample = generator.GenerateCell(cell);

        Assert.Equal(chunk.ElevationMeters[index], sample.ElevationMeters);
        Assert.Equal(chunk.Moisture[index], sample.Moisture);
        Assert.Equal(chunk.ForestCover[index], sample.ForestCover);
        Assert.Equal(chunk.Biome[index], sample.Biome);
    }
}
