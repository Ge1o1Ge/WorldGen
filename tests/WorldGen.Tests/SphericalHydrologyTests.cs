using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SphericalHydrologyTests
{
    private static TerrainCellSample Sample(float elevation) => new(elevation, 20, 0.6f, 0.5f, 0.4f, 1, SphericalBiome.Meadow);

    [Fact]
    public void ShoreContinuesBasinLevelOntoDrySlopesInsteadOfClampingDepthToZero()
    {
        var hydro = SphericalHydrology.FromSamples(12, 0, cell => Sample(cell.Face == CubeFace.NegativeZ ? -1 :
            cell.Face == CubeFace.PositiveZ && cell.Y is >= 4 and <= 7 ? cell.X switch { 4 => 4, 5 => 7, 6 => 12, _ => 10 } : 10));
        var wet = hydro.Index(new(CubeFace.PositiveZ, 5, 5));
        var dry = hydro.Index(new(CubeFace.PositiveZ, 6, 5));
        Assert.Equal(10, hydro.Surface[wet]);
        Assert.Equal(2, hydro.LakeShore[wet]);
        Assert.Equal(-3, hydro.LakeShore[dry]);
        // The bank crosses 40% into the interval (height 9), not its midpoint.
        Assert.Equal(.4f, hydro.LakeShore[wet] / (hydro.LakeShore[wet] - hydro.LakeShore[dry]));
        for (var i = 0; i < hydro.LakeShore.Length; i++)
        {
            Assert.True(float.IsFinite(hydro.LakeShore[i]));
            Assert.Equal(hydro.IsLake(i), hydro.LakeShore[i] > 0);
        }
    }

    [Fact]
    public void RefinementPreservesHistoricalRunoffUnits()
    {
        var hydro = SphericalHydrology.FromSamples(16, 0, cell => Sample(cell.Face == CubeFace.NegativeZ ? -1 : 10), 1f / 16);
        var outlet = hydro.Runoff.Where((_, i) => hydro.Downstream[i] < 0).Sum();
        Assert.InRange(Math.Abs(outlet - hydro.Elevation.Count(e => e > 0) * .6f / 16), 0, .005);
        Assert.Equal(1f / 16, hydro.RunoffWeight);
    }

    [Fact]
    public async Task DefaultGridUsesEverySimulationCellAndMatchesTerrainExactly()
    {
        var source = await WorldGen.Content.SphericalWorldLoader.LoadAsync();
        var definition = source with { FaceSize = 32, ChunkSize = 8 };
        var generator = new SphericalTerrainGenerator(definition);
        var fine = SphericalHydrology.Build(definition, generator);
        var coarse = SphericalHydrology.Build(definition, generator, 4);
        Assert.Equal(32, fine.Resolution); Assert.Equal(8, coarse.Resolution);
        Assert.Equal(1f / 16, fine.RunoffWeight); Assert.Equal(1, coarse.RunoffWeight);
        for (var i = 0; i < fine.Elevation.Length; i++)
            Assert.Equal(generator.GenerateCell(fine.Address(i)).ElevationMeters, fine.Elevation[i]);
    }

    [Fact]
    public void DrainageIsAcyclicConservesRunoffAndCrossesCubeSeams()
    {
        var topology = new CubeSphereTopology(12);
        var hydro = SphericalHydrology.FromSamples(12, 0, cell =>
            Sample((float)(topology.ToUnitVector(cell).Z * 100 + 25)));
        var seamCount = 0;
        for (var index = 0; index < hydro.Downstream.Length; index++)
        {
            var current = index;
            var visited = new HashSet<int>();
            while (hydro.Downstream[current] >= 0)
            {
                Assert.True(visited.Add(current), "Drainage cycle");
                var next = hydro.Downstream[current];
                Assert.True(hydro.Surface[next] <= hydro.Surface[current]);
                Assert.Contains(hydro.Address(next), hydro.GetDrainageNeighbors(hydro.Address(current)));
                current = next;
            }
            Assert.True(hydro.Elevation[current] <= hydro.SeaLevel);
            if (hydro.Downstream[index] >= 0 && hydro.Address(index).Face != hydro.Address(hydro.Downstream[index]).Face)
                seamCount++;
        }
        Assert.True(seamCount > 0);
        var outletRunoff = hydro.Runoff.Where((_, index) => hydro.Downstream[index] < 0).Sum();
        Assert.InRange(Math.Abs(outletRunoff - hydro.Elevation.Count(value => value > 0) * 0.6f), 0, 0.01f);
        Assert.NotEmpty(hydro.BuildReaches(2));
    }

    [Fact]
    public void DepressionStoresSpillSurfaceAndAllLandHasOneDeterministicSink()
    {
        var first = SphericalHydrology.FromSamples(8, 0, cell =>
            Sample(cell == new CellAddress(CubeFace.PositiveZ, 4, 4) ? 1 : 10));
        var second = SphericalHydrology.FromSamples(8, 0, cell =>
            Sample(cell == new CellAddress(CubeFace.PositiveZ, 4, 4) ? 1 : 10));
        Assert.Single(first.Downstream, next => next < 0);
        Assert.Equal(first.Downstream, second.Downstream);
        Assert.Equal(first.Runoff, second.Runoff);

        var basin = SphericalHydrology.FromSamples(8, 0, cell => Sample(cell.Face == CubeFace.NegativeZ
            ? -1 : cell == new CellAddress(CubeFace.PositiveZ, 4, 4) ? 3 : 10));
        var pit = basin.Index(new CellAddress(CubeFace.PositiveZ, 4, 4));
        Assert.Equal(10, basin.Surface[pit]);
        Assert.Equal(3, basin.Elevation[pit]);
        foreach (var reach in basin.BuildReaches(1))
        {
            var current = reach.Id;
            for (var step = 0; step < reach.Points.Count - 1; step++)
            {
                Assert.True(basin.Surface[current] - basin.Elevation[current] <= 1);
                current = basin.Downstream[current];
            }
        }
    }

    [Fact]
    public void CornerTouchingWaterDoesNotActAsABasinSpillway()
    {
        var pitCell = new CellAddress(CubeFace.PositiveZ, 5, 5);
        var diagonalSea = new CellAddress(CubeFace.PositiveZ, 6, 6);
        var hydro = SphericalHydrology.FromSamples(12, 0, cell => Sample(
            cell == diagonalSea ? -1 : cell == pitCell ? 5 : 20));
        var pit = hydro.Index(pitCell);

        // A point contact is not a hydrologic connection: water must cross one
        // of the four 20 m edge-neighbours before reaching the diagonal sea cell.
        Assert.Equal(20, hydro.Surface[pit]);
        Assert.True(hydro.IsLake(pit));
        Assert.All(hydro.Topology.GetNeighbors(pitCell), neighbor =>
            Assert.Equal(20, hydro.Surface[hydro.Index(neighbor)]));
    }

    [Fact]
    public void PointOnlyLakeChainIsRemovedButAnIsolatedPondRemains()
    {
        var first = new CellAddress(CubeFace.PositiveZ, 4, 4);
        var second = new CellAddress(CubeFace.PositiveZ, 5, 5);
        var pond = new CellAddress(CubeFace.PositiveZ, 8, 8);
        var hydro = SphericalHydrology.FromSamples(12, 0, cell => Sample(cell.Face == CubeFace.NegativeZ
            ? -1 : cell == first || cell == second || cell == pond ? 5 : 20));

        Assert.False(hydro.IsLake(hydro.Index(first)));
        Assert.False(hydro.IsLake(hydro.Index(second)));
        Assert.True(hydro.IsLake(hydro.Index(pond)));
    }
}
