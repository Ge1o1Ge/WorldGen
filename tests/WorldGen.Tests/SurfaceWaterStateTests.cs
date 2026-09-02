using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SurfaceWaterStateTests
{
    private static TerrainCellSample Sample(float elevation) =>
        new(elevation, 15, .5f, .5f, .3f, 1, elevation <= 0 ? SphericalBiome.Ocean : SphericalBiome.Meadow);

    [Fact]
    public void ChannelSedimentUsesOneCubicMeterPerHundredCubicMetersOfFlow()
    {
        Assert.Equal(1, SurfaceWaterState.ErodedSedimentCubicMeters(100));
        Assert.Equal(12.5, SurfaceWaterState.ErodedSedimentCubicMeters(1_250));
        Assert.Equal(0, SurfaceWaterState.ErodedSedimentCubicMeters(-100));
        Assert.Equal(4, SurfaceWaterState.StandingWaterDepositRadiusCells(100));
        Assert.Equal(5, SurfaceWaterState.StandingWaterDepositRadiusCells(1_000));
        Assert.Equal(9, SurfaceWaterState.StandingWaterDepositRadiusCells(10_000_000));
    }

    [Fact]
    public void BelowSeaDepressionIsDryUntilItConnectsToTheOcean()
    {
        const int size = 8;
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.PositiveZ && cell.X <= 1 ? -10 : 10));
        var water = SurfaceWaterState.FromHydrology(hydro, 100);
        var crater = new CellAddress(CubeFace.PositiveZ, 4, 4);

        var isolated = water.ApplyTerrainChanges(new Dictionary<CellAddress, int> { [crater] = -2_000 });

        Assert.False(water.IsOcean(crater));
        Assert.False(water.IsWet(crater));
        Assert.Equal(0, isolated.OceanCellsAdded);

        var connected = water.ApplyTerrainChanges(new Dictionary<CellAddress, int>
        {
            [new(CubeFace.PositiveZ, 2, 4)] = -2_000,
            [new(CubeFace.PositiveZ, 3, 4)] = -2_000
        });

        Assert.True(water.IsOcean(crater));
        Assert.Equal(10, water.DepthAt(crater));
        Assert.Equal(3, connected.OceanCellsAdded);
        Assert.True(connected.OceanVolumeDeltaCubicMeters > 0);
        Assert.Contains(crater, connected.ChangedWaterCells);
        Assert.Contains(topologyNeighbor(new(CubeFace.PositiveZ, 3, 4)), connected.ChangedWaterCells);

        CellAddress topologyNeighbor(CellAddress cell) =>
            hydro.Topology.GetNeighbor(cell, CardinalDirection.North);
    }

    [Fact]
    public void LargestConnectedBelowSeaComponentDefinesTheOceanAcrossCubeSeams()
    {
        const int size = 8;
        var topology = new CubeSphereTopology(size);
        var coast = new CellAddress(CubeFace.PositiveX, size - 1, 3);
        var seamNeighbor = topology.GetNeighbor(coast, CardinalDirection.East);
        var isolated = new CellAddress(CubeFace.NegativeZ, 4, 4);
        var wet = new HashSet<CellAddress> { coast, seamNeighbor };
        foreach (var neighbor in topology.GetNeighbors(coast)) wet.Add(neighbor);
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(wet.Contains(cell) || cell == isolated ? -4 : 12));

        var water = SurfaceWaterState.FromHydrology(hydro, 100);

        Assert.True(water.IsOcean(coast));
        Assert.True(water.IsOcean(seamNeighbor));
        Assert.False(water.IsOcean(isolated));
        Assert.False(water.IsWet(isolated));
    }

    [Fact]
    public void ExistingDrainageLakeAndOceanShareOneSignedWaterField()
    {
        var hydro = SphericalHydrology.FromSamples(12, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -5 :
            cell.Face == CubeFace.PositiveZ && cell.Y is >= 4 and <= 7
                ? cell.X switch { 4 => 4, 5 => 7, 6 => 12, _ => 10 }
                : 10));
        var water = SurfaceWaterState.FromHydrology(hydro, 100);
        var lake = new CellAddress(CubeFace.PositiveZ, 5, 5);
        var bank = new CellAddress(CubeFace.PositiveZ, 6, 5);
        var diagonalBank = new CellAddress(CubeFace.PositiveZ, 6, 4);
        var diagonalOnly = new CellAddress(CubeFace.PositiveZ, 6, 3);

        Assert.False(water.IsOcean(lake));
        Assert.True(water.IsWet(lake));
        Assert.True(water.IsOpenWater(lake));
        Assert.True(water.ShoreAt(lake) > 0);
        Assert.True(water.ShoreAt(bank) < 0);
        Assert.InRange(water.ShoreAt(diagonalBank), -63.99f, -.001f);
        Assert.InRange(water.ShoreAt(diagonalOnly), -63.99f, -.001f);
        Assert.True(water.IsOcean(new(CubeFace.NegativeZ, 5, 5)));
    }

    [Fact]
    public void RainInfiltrationRunoffAndEvaporationKeepTheDailyMassBalance()
    {
        const int size = 8;
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -10 : 100 + cell.X * 4 + cell.Y));
        var water = SurfaceWaterState.FromHydrology(hydro, 100);
        var source = new CellAddress(CubeFace.PositiveZ, 5, 4);
        var rain = new float[6 * size * size];
        var evaporation = new float[rain.Length];
        rain[water.Index(source)] = 20;

        var result = water.AdvanceDay(new(size, rain, evaporation));

        Assert.Equal(2, result.PrecipitationCubicMeters, 5);
        Assert.Equal(2, result.InfiltrationCubicMeters, 5);
        Assert.Equal(0, result.GroundwaterRechargeCubicMeters, 5);
        Assert.Equal(0, water.DepthAt(source), 5);
        Assert.InRange(Math.Abs(result.BalanceErrorCubicMeters), 0, .02);
        Assert.True(water.SoilWater[water.Index(source)] > 0);

        double recharge = 0;
        for (var day = 0; day < 6; day++)
            recharge += water.AdvanceDay(new(size, rain, evaporation)).GroundwaterRechargeCubicMeters;
        Assert.True(recharge > 0);
        Assert.Equal(0, water.DepthAt(source), 5);

        Array.Clear(rain);
        Array.Fill(evaporation, 2);
        var dry = water.AdvanceDay(new(size, rain, evaporation));
        Assert.True(dry.EvaporationCubicMeters > 0);
        Assert.InRange(Math.Abs(dry.BalanceErrorCubicMeters), 0, .02);
    }

    [Fact]
    public void SurfaceRunoffCrossesACubeFaceSeamWithoutCreatingWater()
    {
        const int size = 8;
        var topology = new CubeSphereTopology(size);
        var source = new CellAddress(CubeFace.PositiveX, size - 1, 4);
        var target = topology.GetNeighbor(source, CardinalDirection.East);
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -10 : cell == source ? 20 : cell == target ? 5 : 30));
        var water = SurfaceWaterState.FromHydrology(hydro, 100);
        var rain = new float[6 * size * size];
        rain[water.Index(source)] = 300;

        var result = water.AdvanceDay(new(size, rain, new float[rain.Length]));

        Assert.True(water.DepthAt(target) > 0);
        Assert.InRange(Math.Abs(result.BalanceErrorCubicMeters), 0, .02);
    }

    [Fact]
    public void TerrainCutBelowTheLocalWaterTableCreatesAFiniteSpring()
    {
        const int size = 8;
        var hydro = SphericalHydrology.FromSamples(size, 0, cell =>
            new TerrainCellSample(cell.Face == CubeFace.NegativeZ ? -10 : 100, 15, .8f, .5f, .3f, 1,
                cell.Face == CubeFace.NegativeZ ? SphericalBiome.Ocean : SphericalBiome.Meadow));
        var water = SurfaceWaterState.FromHydrology(hydro, 100);
        var crater = new CellAddress(CubeFace.PositiveZ, 4, 4);
        var headBefore = water.GroundwaterHeadAt(crater);
        water.ApplyTerrainChanges(new Dictionary<CellAddress, int> { [crater] = -1_000 });

        var result = water.AdvanceDay(new(size, new float[6 * size * size], new float[6 * size * size]));

        Assert.True(headBefore > water.Elevation[water.Index(crater)]);
        Assert.True(result.SpringDischargeCubicMeters > 0);
        Assert.True(water.DepthAt(crater) > 0);
        Assert.InRange(Math.Abs(result.BalanceErrorCubicMeters), 0, .02);
    }

    [Fact]
    public void SustainedRunoffBuildsAStableCalculatedChannelAndReroutesWithHysteresis()
    {
        const int size = 8;
        var topology = new CubeSphereTopology(size);
        var source = new CellAddress(CubeFace.PositiveZ, 3, 4);
        var east = topology.GetNeighbor(source, CardinalDirection.East);
        var west = topology.GetNeighbor(source, CardinalDirection.West);
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -10 : 120 - cell.X * 5));
        var water = SurfaceWaterState.FromHydrology(hydro, 10_000);
        var rain = new float[6 * size * size];
        rain[water.Index(source)] = 400;
        var forcing = new SurfaceWaterWeatherForcing(size, rain, new float[rain.Length]);

        IReadOnlyDictionary<CellAddress, int> terrainChanges = new Dictionary<CellAddress, int>();
        for (var day = 0; day < 30; day++)
        {
            var step = water.AdvanceDay(forcing);
            if (step.ChannelTerrainChanges.Count > 0)
            {
                terrainChanges = step.ChannelTerrainChanges;
                water.ApplyTerrainChanges(terrainChanges);
            }
        }

        Assert.Equal(water.Index(east), water.ChannelTarget[water.Index(source)]);
        var reach = Assert.Single(water.BuildRiverReaches(), reach => reach.Id == water.Index(source));
        Assert.True(reach.DischargeCubicMetersPerDay >= SurfaceWaterState.MinimumChannelDischargeCubicMetersPerDay);
        Assert.NotEqual(DynamicRiverClass.None, reach.Class);
        Assert.False(water.IsOpenWater(source));
        Assert.True(water.ChannelIncision[water.Index(source)] >= .015f);
        Assert.NotEmpty(terrainChanges);
        Assert.Equal(0, terrainChanges.Values.Sum());

        water.ApplyTerrainChanges(new Dictionary<CellAddress, int>
        {
            [east] = 2_000,
            [west] = -2_000
        });
        for (var day = 0; day < 6; day++) water.AdvanceDay(forcing);
        Assert.Equal(water.Index(east), water.ChannelTarget[water.Index(source)]);

        water.AdvanceDay(forcing);
        Assert.Equal(water.Index(west), water.ChannelTarget[water.Index(source)]);
    }

    [Fact]
    public void WeakIncisedDrainageRemainsInTheCoreButIsNotSentAsARiver()
    {
        const int size = 8;
        var source = new CellAddress(CubeFace.PositiveZ, 3, 4);
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -10 : 120 - cell.X * 5));
        var water = SurfaceWaterState.FromHydrology(hydro, 10_000);
        var rain = new float[6 * size * size];
        rain[water.Index(source)] = 75;
        var forcing = new SurfaceWaterWeatherForcing(size, rain, new float[rain.Length]);

        // A weak branch now cuts the bed by transported volume rather than an
        // arbitrary monthly minimum; it needs more than one month to reach the
        // 1.5 cm persistent-channel threshold.
        for (var day = 0; day < 60; day++)
        {
            var step = water.AdvanceDay(forcing);
            if (step.ChannelTerrainChanges.Count > 0) water.ApplyTerrainChanges(step.ChannelTerrainChanges);
        }

        var index = water.Index(source);
        Assert.True(water.ChannelIncision[index] >= .015f);
        Assert.InRange(water.SmoothedDischarge[index],
            SurfaceWaterState.MinimumChannelDischargeCubicMetersPerDay,
            SurfaceWaterState.MinimumRenderedRiverDischargeCubicMetersPerDay - .001f);
        Assert.DoesNotContain(water.BuildRiverReaches(), reach => reach.Id == index);
    }

    [Fact]
    public void CalculatedRiverEndsAtTheSignedShoreOfAStandingWaterBody()
    {
        const int size = 12;
        var source = new CellAddress(CubeFace.PositiveZ, 6, 5);
        var hydro = SphericalHydrology.FromSamples(size, 0, cell => Sample(
            cell.Face == CubeFace.NegativeZ ? -10 :
            cell.Face == CubeFace.PositiveZ && cell.Y is >= 4 and <= 7
                ? cell.X switch { 4 => 4, 5 => 7, 6 => 12, _ => 10 }
                : 10));
        var water = SurfaceWaterState.FromHydrology(hydro, 10_000);
        var rain = new float[6 * size * size];
        rain[water.Index(source)] = 500;
        for (var day = 0; day < 30; day++)
        {
            var step = water.AdvanceDay(new(size, rain, new float[rain.Length]));
            if (step.ChannelTerrainChanges.Count > 0) water.ApplyTerrainChanges(step.ChannelTerrainChanges);
        }

        var reach = Assert.Single(water.BuildRiverReaches(), value => value.Id == water.Index(source));
        var end = reach.Points[^1];
        var dry = hydro.Topology.ToUnitVector(source);
        var lake = new CellAddress(CubeFace.PositiveZ, 5, 5);
        var wet = hydro.Topology.ToUnitVector(lake);

        Assert.True(water.IsOpenWater(lake));
        Assert.True(end.Dot(dry) < .999999999 || end != dry);
        Assert.True(end.Dot(wet) < .999999999 || end != wet);
        Assert.True(end.Dot(dry) > dry.Dot(wet));
        Assert.True(end.Dot(wet) > dry.Dot(wet));
    }
}
