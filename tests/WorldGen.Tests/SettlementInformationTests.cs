using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed class SettlementInformationTests
{
    private static async Task<(WorldState World, WorldGen.Core.Content.ContentCatalog Content)> Create()
    {
        var content = await ContentLoader.LoadAsync(); var world = WorldFactory.Create(content);
        world.Routes.Clear();
        var addresses = world.Spatial.Territories.ToDictionary(p => p.Key, p => new CellAddress(CubeFace.PositiveZ, p.Value.Grid.X, p.Value.Grid.Y));
        SettlementInformation.Initialize(world, addresses);
        return (world, content);
    }

    [Fact]
    public async Task LocalEventsAreKnownImmediatelyButDoNotRevealOtherSettlementsOrTeachTechnology()
    {
        var (world, _) = await Create(); var cities = world.Cities.Values.ToArray();
        var technologies = JsonSerializer.Serialize(cities[1].TechnologyState);
        var evt = Journal.Record(world, "household_discovery", cities[0].Id, details: new JsonObject { ["cityId"] = cities[0].Id });
        Assert.Contains(evt.Id, cities[0].KnowledgeState.Observations.Keys);
        Information.Advance(world);
        Assert.DoesNotContain(evt.Id, cities[1].KnowledgeState.Observations.Keys);
        Assert.All(cities, c => Assert.Equal(c.Id, Assert.Single(c.KnowledgeState.KnownSettlements!).Key));
        Assert.Equal(technologies, JsonSerializer.Serialize(cities[1].TechnologyState));
    }

    [Fact]
    public async Task FoundersCarryDepartureKnowledgeAndParentLearnsDestinationOnlyAfterReturnReport()
    {
        var (world, content) = await Create(); var cities = world.Cities.Values.ToArray();
        var parent = cities[0]; var child = cities[1]; var third = cities[2];
        var bundle = SettlementInformation.CaptureKnownWorld(parent, world.Day);
        SettlementInformation.Receive(parent, third.KnowledgeState.KnownSettlements![third.Id], world.Day, third.Id, "traveler", 1);
        world.Day = 5;
        SettlementInformation.CarryKnownWorld(bundle, child, world.Day);
        Assert.Contains(parent.Id, child.KnowledgeState.KnownSettlements!.Keys);
        Assert.DoesNotContain(third.Id, child.KnowledgeState.KnownSettlements.Keys);
        Assert.DoesNotContain(child.Id, parent.KnowledgeState.KnownSettlements!.Keys);
        var technologies = JsonSerializer.Serialize(parent.TechnologyState);
        var report = SettlementInformation.SendContactReport(world, child.Id, parent.Id, child.Id, 3);
        var restored = WorldSnapshot.Restore(content, WorldSnapshot.Create(world));
        Assert.Equal(WorldSnapshot.Hash(world), WorldSnapshot.Hash(restored));
        world.Day = restored.Day = 7; Information.Advance(world); Information.Advance(restored);
        Assert.DoesNotContain(child.Id, parent.KnowledgeState.KnownSettlements.Keys);
        world.Day = restored.Day = 8; Information.Advance(world); Information.Advance(restored);
        var place = parent.KnowledgeState.KnownSettlements[child.Id];
        Assert.Equal(5, place.ObservedDay); Assert.Equal(8, place.ReceivedDay); Assert.Equal("traveler", place.Channel);
        Assert.Equal(report.Settlement!.Cell, place.Cell);
        Assert.Equal(technologies, JsonSerializer.Serialize(parent.TechnologyState));
        Assert.Equal(WorldSnapshot.Hash(world), WorldSnapshot.Hash(restored));
    }

    [Fact]
    public async Task ReportsAreDatedSnapshotsAndOlderReportsCannotOverwriteNewerKnowledge()
    {
        var (world, _) = await Create(); var cities = world.Cities.Values.ToArray(); var a = cities[0]; var b = cities[1];
        var place = b.KnowledgeState.KnownSettlements![b.Id];
        SettlementInformation.Receive(a, place with { Name = "Новые сведения", ObservedDay = 10 }, 12, b.Id, "traveler", .9);
        SettlementInformation.Receive(a, place with { Name = "Старый слух", ObservedDay = 3 }, 13, b.Id, "rumor", 1);
        Assert.Equal("Новые сведения", a.KnowledgeState.KnownSettlements![b.Id].Name);
        Assert.Throws<InvalidOperationException>(() => SettlementInformation.SendContactReport(world, b.Id, a.Id, b.Id, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettlementInformation.SendContactReport(world, a.Id, b.Id, b.Id, 0));
    }
}
