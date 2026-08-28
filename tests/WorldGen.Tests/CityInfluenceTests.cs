using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class CityInfluenceTests
{
    [Fact]
    public void BuildingsAndUsedFieldsBothShapeTheBoundary()
    {
        var topology = new CubeSphereTopology(16);
        var city = "city:a";
        var building = new CellAddress(CubeFace.PositiveZ, 7, 7);
        var remoteField = new CellAddress(CubeFace.PositiveZ, 12, 7);
        var influence = CityInfluenceEngine.Build(
            topology,
            [
                new CityInfluenceSource("house:a", city, building, CityAssetKind.Building, 1),
                new CityInfluenceSource("field:a", city, remoteField, CityAssetKind.CultivatedField, 0.55f)
            ],
            _ => 1,
            new CityInfluenceSettings(0.1f, 0.05f));

        Assert.Equal(city, influence.Cells[building].CityId);
        Assert.Equal(city, influence.Cells[remoteField].CityId);
        Assert.Contains(new CellAddress(CubeFace.PositiveZ, 14, 7), influence.Cells.Keys);
        Assert.DoesNotContain(new CellAddress(CubeFace.PositiveZ, 0, 0), influence.Cells.Keys);
        Assert.NotEmpty(influence.BoundaryCells);
    }

    [Fact]
    public void ClaimsCrossSphereSeamsAndRespectImpassableTerrain()
    {
        var topology = new CubeSphereTopology(12);
        var edge = new CellAddress(CubeFace.PositiveX, 11, 6);
        var acrossSeam = topology.GetNeighbor(edge, CardinalDirection.East);
        var blocked = topology.GetNeighbor(edge, CardinalDirection.West);
        var influence = CityInfluenceEngine.Build(
            topology,
            [new CityInfluenceSource("tower", "city:a", edge, CityAssetKind.Building, 0.5f)],
            cell => cell == blocked ? float.PositiveInfinity : 1,
            new CityInfluenceSettings(0.1f, 0.05f));

        Assert.Contains(acrossSeam, influence.Cells.Keys);
        Assert.DoesNotContain(blocked, influence.Cells.Keys);
    }

    [Fact]
    public void StrongerClaimWinsAndTieBreakIsDeterministic()
    {
        var topology = new CubeSphereTopology(8);
        var contested = new CellAddress(CubeFace.PositiveZ, 4, 4);
        var influence = CityInfluenceEngine.Build(
            topology,
            [
                new CityInfluenceSource("b", "city:b", contested, CityAssetKind.Building, 0.8f),
                new CityInfluenceSource("a", "city:a", contested, CityAssetKind.Building, 0.8f)
            ],
            _ => 1);

        Assert.Equal("city:a", influence.Cells[contested].CityId);
    }
}
