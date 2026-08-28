using WorldGen.Content;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SphericalSettlementLayerTests
{
    [Fact]
    public async Task ConfiguredBuildingsAndLandsProduceDynamicClaims()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var topology = new CubeSphereTopology(definition.FaceSize);
        var terrain = new SphericalTerrainGenerator(definition);

        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);

        Assert.Equal(4, layer.Construction.Buildings.Count);
        Assert.Equal(6, layer.UsedLands.Count);
        Assert.NotEmpty(layer.Influence.Cells);
        Assert.All(definition.Settlements, settlement =>
            Assert.Contains(layer.Influence.Cells.Values, cell => cell.CityId == settlement.Id));
        Assert.NotEmpty(layer.Influence.BoundaryCells);
    }

    [Fact]
    public async Task RemovingBuildingChangesTheNextInfluenceSourceSet()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var topology = new CubeSphereTopology(definition.FaceSize);
        var terrain = new SphericalTerrainGenerator(definition);
        var layer = SphericalSettlementLayer.Build(definition, topology, terrain);
        var before = layer.Construction.ToInfluenceSources().Count();
        var revision = layer.Revision;
        var oldMap = layer.Influence;

        Assert.True(layer.Construction.Remove("river_mill"));

        Assert.True(layer.Construction.ToInfluenceSources().Count() < before);
        Assert.True(layer.Revision > revision);
        Assert.NotSame(oldMap, layer.Influence);
    }

    [Fact]
    public async Task AbandonedLandStopsEmittingAndReactivationRestoresTheSameClaims()
    {
        var definition = await SphericalWorldLoader.LoadAsync();
        var layer = SphericalSettlementLayer.Build(definition, new CubeSphereTopology(definition.FaceSize),
            new SphericalTerrainGenerator(definition));
        var original = layer.Influence;
        var revision = layer.Revision;
        Assert.Same(original, layer.Influence);
        Assert.Equal(revision, layer.Revision);
        var parcel = layer.UsedLands.First(land => land.Id == "river_fields_e");

        Assert.True(layer.SetLandUsage(parcel.Id, 0));
        var abandoned = layer.Influence;
        Assert.True(layer.Revision > revision);
        Assert.True(abandoned.Cells.Count < original.Cells.Count);
        Assert.Equal(0, layer.UsedLands.First(land => land.Id == parcel.Id).Usage);

        Assert.True(layer.SetLandUsage(parcel.Id, parcel.Usage));
        var restored = layer.Influence;
        Assert.Equal(original.Cells.Count, restored.Cells.Count);
        foreach (var (cell, claim) in original.Cells) Assert.Equal(claim, restored.Cells[cell]);
        Assert.False(layer.SetLandUsage("missing", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetLandUsage(parcel.Id, float.NaN));
    }
}
