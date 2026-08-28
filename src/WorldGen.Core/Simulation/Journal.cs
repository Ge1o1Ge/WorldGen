using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public static class Journal
{
    public static JournalEvent Record(
        WorldState world,
        string type,
        string? subjectId = null,
        IEnumerable<string?>? causeIds = null,
        JsonObject? details = null)
    {
        var entry = new JournalEvent(
            $"event-{world.NextEventId:000000}",
            world.Day,
            type,
            subjectId,
            (causeIds ?? []).Where(id => id is not null).Select(id => id!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList(),
            details ?? []);
        world.NextEventId++;
        world.Journal.Add(entry);
        SettlementInformation.ObserveLocal(world, entry);
        return entry;
    }
}
