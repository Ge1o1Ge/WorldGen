using System.Text.Json.Nodes;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed record SettlementKnowledge(string CityId, string Name, CellAddress Cell, int ObservedDay,
    int ReceivedDay, string SourceCityId, string Channel, double Confidence);
public sealed record WorldKnowledgeBundle(string SourceCityId, int DepartureDay, IReadOnlyList<SettlementKnowledge> Places);

/// <summary>Place reports are dated claims, never references to the live world.
/// Skills and productive adoption are deliberately not copied by this subsystem.</summary>
public static class SettlementInformation
{
    public static void Initialize(WorldState world, IReadOnlyDictionary<string, CellAddress> addresses)
    {
        foreach (var city in world.Cities.Values)
        {
            if (city.KnowledgeState.KnownSettlements is not null) continue;
            var anchor = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
            city.KnowledgeState.KnownSettlements = new(StringComparer.Ordinal)
            { [city.Id] = new(city.Id, city.Name, anchor, world.Day, world.Day, city.Id, "direct", 1) };
            // Upgrade old spherical snapshots using only events inside this city;
            // the observer's global journal does not reveal other settlements.
            foreach (var evt in world.Journal.Where(e => e.Details["cityId"]?.GetValue<string>() == city.Id))
                ObserveLocal(world, evt);
        }
    }

    public static void ObserveLocal(WorldState world, JournalEvent evt)
    {
        var cityId = evt.Details["cityId"]?.GetValue<string>();
        if (cityId is null || !world.Cities.TryGetValue(cityId, out var city) || city.KnowledgeState.KnownSettlements is null) return;
        city.KnowledgeState.Observations.TryAdd(evt.Id, new(evt.Id, cityId, evt.Day, 1, "direct", null));
    }

    public static void Receive(CityState recipient, SettlementKnowledge? report, int day, string source, string channel, double confidence)
    {
        if (report is null || report.ObservedDay > day || recipient.KnowledgeState.KnownSettlements is not { } places) return;
        if (places.TryGetValue(report.CityId, out var known) &&
            (known.ObservedDay > report.ObservedDay || known.ObservedDay == report.ObservedDay && known.Confidence > confidence)) return;
        places[report.CityId] = report with { ReceivedDay = day, SourceCityId = source, Channel = channel, Confidence = confidence };
    }

    // Founders carry a snapshot of the world they knew on departure. This does
    // not teach their parent settlement about the destination or copy machines.
    public static WorldKnowledgeBundle CaptureKnownWorld(CityState source, int day) =>
        new(source.Id, day, source.KnowledgeState.KnownSettlements?.Values.ToArray() ?? []);

    public static void CarryKnownWorld(WorldKnowledgeBundle bundle, CityState destination, int day)
    {
        if (day < bundle.DepartureDay) throw new ArgumentOutOfRangeException(nameof(day));
        foreach (var place in bundle.Places)
            if (place.CityId != destination.Id) Receive(destination, place, day, bundle.SourceCityId, "founders", place.Confidence);
    }

    // Transport integration point: a caller must have an actual travel plan.
    // No automatic acknowledgement or reciprocal discovery is generated here.
    public static InformationReport SendContactReport(WorldState world, string from, string to, string subject, int travelDays)
    {
        if (travelDays < 1) throw new ArgumentOutOfRangeException(nameof(travelDays));
        var sender = world.Cities[from];
        if (!world.Cities.ContainsKey(to) || sender.KnowledgeState.KnownSettlements is not { } places ||
            !places.ContainsKey(to) || !places.TryGetValue(subject, out var place))
            throw new InvalidOperationException("Отправитель не знает адресата или описываемое поселение");
        var arrivalDay = checked(world.Day + travelDays);
        var evt = Journal.Record(world, "settlement_contact_sent", from, details: new JsonObject { ["cityId"] = from, ["to"] = to });
        var report = new InformationReport($"report-{world.Information.NextReportId++:000000}", evt.Id, "settlement_contact", world.Day,
            from, to, world.Day, arrivalDay, place.Confidence, "traveler",
            subject == from ? place with { ObservedDay = world.Day } : place);
        world.Information.Reports.Add(report);
        return report;
    }
}
