using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    [Fact]
    public async Task JournalAndObservationsAreCompactedIntoABoundedArchive()
    {
        var simulation = await CreateScouting(pressure: false);
        var world = simulation.World;
        var city = world.Cities.Values.OrderBy(item => item.Id, StringComparer.Ordinal).First();

        for (var index = 0; index < 6_000; index++)
        {
            world.Day = index;
            Journal.Record(world, "diagnostic_event", city.Id,
                details: new JsonObject { ["cityId"] = city.Id, ["value"] = index });
        }
        world.Information.LastJournalIndex = world.Journal.Count;

        WorldHistory.Compact(world, force: true);

        Assert.True(world.Journal.Count <= WorldHistory.JournalSoftLimit);
        Assert.Equal(6_000 - world.Journal.Count, world.JournalArchive.RemovedEvents);
        Assert.Equal(world.JournalArchive.RemovedEvents, world.JournalArchive.CountsByType["diagnostic_event"]);
        Assert.True(city.KnowledgeState.Observations.Count <= WorldHistory.ObservationSoftLimitPerCity);
        Assert.Equal(world.Journal.Count, world.Information.LastJournalIndex);

        var restored = WorldSnapshot.Restore(simulation.Content, WorldSnapshot.Create(world));
        Assert.Equal(world.JournalArchive.RemovedEvents, restored.JournalArchive.RemovedEvents);
        Assert.Equal(world.Journal.Count, restored.Journal.Count);
    }
}
