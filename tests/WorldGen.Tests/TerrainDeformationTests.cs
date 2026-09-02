using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class TerrainDeformationTests
{
    [Fact]
    public void Impact_crosses_cube_seams_and_balances_excavated_ground_to_one_centimeter()
    {
        var topology = new CubeSphereTopology(32); var state = new TerrainDeformationState(topology);
        var result = state.Impact(new CellAddress(CubeFace.PositiveX, 0, 16), 5, 12.34);
        Assert.Equal(0, result.BalanceErrorCentimeters);
        Assert.Contains(result.DeltaCentimeters, pair => pair.Key.Face != CubeFace.PositiveX);
        Assert.True(result.DeltaCentimeters.Values.Min() <= -1234);
        Assert.True(result.DeltaCentimeters.Values.Max() > 0);
        Assert.Equal(-12.34f, state.Apply(new CellAddress(CubeFace.PositiveX, 0, 16), 0), 2);
    }

    [Fact]
    public void Repeated_impacts_accumulate_in_sparse_centimeter_offsets()
    {
        var topology = new CubeSphereTopology(32); var state = new TerrainDeformationState(topology);
        var center = new CellAddress(CubeFace.PositiveZ, 12, 12);
        state.Impact(center, 3, 1.25); state.Impact(center, 3, 1.25);
        Assert.Equal(2u, state.Revision); Assert.Equal(-2.5f, state.Apply(center, 0), 2);
    }

    [Fact]
    public void Impact_uses_spherical_radius_instead_of_a_square_graph_radius()
    {
        var topology = new CubeSphereTopology(64); var state = new TerrainDeformationState(topology);
        var center = new CellAddress(CubeFace.PositiveZ, 32, 32);
        var result = state.Impact(center, 8, 20);
        var axis = Math.Abs(result.DeltaCentimeters[new(CubeFace.PositiveZ, 37, 32)]);
        var diagonal = Math.Abs(result.DeltaCentimeters[new(CubeFace.PositiveZ, 36, 35)]);

        Assert.InRange(diagonal / (double)axis, .72, 1.28);
        Assert.Equal(0, result.BalanceErrorCentimeters);
    }
}
