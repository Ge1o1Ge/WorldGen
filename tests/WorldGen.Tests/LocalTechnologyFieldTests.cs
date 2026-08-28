using WorldGen.Core.Knowledge;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class LocalTechnologyFieldTests
{
    [Fact]
    public void ImplementedTechnologySpreadsFarMoreThanUnpracticedKnowledge()
    {
        var topology = new CubeSphereTopology(12);
        var source = new CellAddress(CubeFace.PositiveZ, 5, 5);
        var neighbor = topology.GetNeighbor(source, CardinalDirection.East);
        var unpracticed = new LocalTechnologyField(topology);
        unpracticed.SetState("mill", source, new LocalTechnologyState
            { Knowledge = 1, Competence = 1, Capability = 1, Adoption = 0 });
        var practiced = new LocalTechnologyField(topology);
        practiced.SetState("mill", source, new LocalTechnologyState
            { Knowledge = 1, Competence = 1, Capability = 1, Adoption = 1 });

        unpracticed.DiffuseKnowledge("mill");
        practiced.DiffuseKnowledge("mill");

        var weak = unpracticed.GetState("mill", neighbor).Knowledge;
        var strong = practiced.GetState("mill", neighbor).Knowledge;
        Assert.True(weak > 0);
        Assert.True(strong > weak * 30);
    }

    [Fact]
    public void KnowledgeDoesNotAutomaticallyCreateCompetenceCapabilityOrAdoption()
    {
        var topology = new CubeSphereTopology(10);
        var field = new LocalTechnologyField(topology);
        var source = new CellAddress(CubeFace.PositiveX, 9, 5);
        var neighbor = topology.GetNeighbor(source, CardinalDirection.East);
        field.SetState("mill", source, new LocalTechnologyState
            { Knowledge = 1, Competence = 1, Capability = 1, Adoption = 1 });

        field.DiffuseKnowledge("mill");
        var state = field.GetState("mill", neighbor);

        Assert.True(state.Knowledge > 0);
        Assert.Equal(0, state.Competence);
        Assert.Equal(0, state.Capability);
        Assert.Equal(0, state.Adoption);
    }
}
