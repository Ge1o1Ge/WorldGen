using WorldGen.Core.Simulation;

namespace WorldGen.Tests;

public sealed class NestedNodeKernelTests
{
    [Fact]
    public void SleepingNodeReusesResultInsideStableInputCorridor()
    {
        var node = new TestNode();
        var kernel = new NestedNodeKernel();

        var first = kernel.Execute(node, Frame(10, 100));
        var second = kernel.Execute(node, Frame(11, 103));

        Assert.False(first.UsedCachedResult);
        Assert.True(second.UsedCachedResult);
        Assert.Equal(1, node.Evaluations);
    }

    [Fact]
    public void EventOrCorridorCrossingUnfoldsNode()
    {
        var node = new TestNode();
        var kernel = new NestedNodeKernel();
        kernel.Execute(node, Frame(10, 100));

        var changed = kernel.Execute(node, Frame(11, 111));
        var eventDriven = kernel.Execute(node, Frame(12, 112, "fire"));

        Assert.False(changed.UsedCachedResult);
        Assert.False(eventDriven.UsedCachedResult);
        Assert.Equal(3, node.Evaluations);
    }

    [Fact]
    public void ScheduledWakeRunsNodeEvenWithStableInputs()
    {
        var node = new TestNode();
        var kernel = new NestedNodeKernel();
        kernel.Execute(node, Frame(10, 100));

        var woke = kernel.Execute(node, Frame(15, 101));

        Assert.False(woke.UsedCachedResult);
        Assert.Equal(5, node.LastElapsedDays);
    }

    private static NodeInputFrame Frame(int day, double food, params string[] events) =>
        new(day, [new NodeInputSignal("food", food, 10)], events);

    private sealed class TestNode : INestedSimulationNode
    {
        public string Id => "city:test";
        public int Evaluations { get; private set; }
        public int LastElapsedDays { get; private set; }

        public NodeEvaluation Evaluate(NodeInputFrame input, int elapsedDays)
        {
            Evaluations++;
            LastElapsedDays = elapsedDays;
            return new NodeEvaluation(
                new Dictionary<string, double> { ["food"] = input.Signals[0].Value * 0.9 },
                Evaluations,
                input.Day + 5,
                NodeActivityMode.Sleeping);
        }
    }
}
