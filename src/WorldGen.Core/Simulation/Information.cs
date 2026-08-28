namespace WorldGen.Core.Simulation;

public static class Information
{
    private static readonly HashSet<string> ReportableTypes = new(StringComparer.Ordinal)
    {
        "crisis_started", "food_shortage_started", "resource_shortage_started", "technology_milestone",
        "migration_flow", "price_shock_started"
    };

    public static void Advance(WorldState world)
    {
        Receive(world);
        Schedule(world);
    }

    private static void Receive(WorldState world)
    {
        var arrived = world.Information.Reports.Where(report => report.ArrivalDay <= world.Day)
            .OrderBy(report => report.Id, StringComparer.Ordinal).ToArray();
        var ids = arrived.Select(report => report.Id).ToHashSet(StringComparer.Ordinal);
        world.Information.Reports = world.Information.Reports.Where(report => !ids.Contains(report.Id)).ToList();
        foreach (var report in arrived)
        {
            world.Cities[report.To].KnowledgeState.Observations[report.EventId] =
                new ObservationState(report.EventId, report.SourceCityId, world.Day, report.Confidence, report.Channel, report.Id);
            SettlementInformation.Receive(world.Cities[report.To], report.Settlement, world.Day, report.SourceCityId, report.Channel, report.Confidence);
            if (report.EventType is "crisis_started" or "technology_milestone")
                Journal.Record(world, "information_received", report.To, [report.EventId],
                    new System.Text.Json.Nodes.JsonObject { ["cityId"] = report.To, ["sourceCityId"] = report.SourceCityId,
                        ["reportedEventId"] = report.EventId, ["reportedEventType"] = report.EventType,
                        ["delayDays"] = world.Day - report.EventDay, ["confidence"] = report.Confidence });
        }
    }

    private static void Schedule(WorldState world)
    {
        foreach (var evt in world.Journal.Skip(world.Information.LastJournalIndex).ToArray())
        {
            if (!ReportableTypes.Contains(evt.Type)) continue;
            var source = evt.Details["cityId"]?.GetValue<string>() ?? evt.Details["from"]?.GetValue<string>();
            if (source is null || !world.Cities.ContainsKey(source)) continue;
            world.Cities[source].KnowledgeState.Observations[evt.Id] =
                new ObservationState(evt.Id, source, world.Day, 1, "direct", null);
            foreach (var to in world.Cities.Keys.Order(StringComparer.Ordinal))
            {
                if (to == source) continue;
                var travelDays = ShortestTravelDays(world.Routes, source, to);
                if (travelDays is null) continue;
                world.Information.Reports.Add(new InformationReport(
                    $"report-{world.Information.NextReportId:000000}", evt.Id, evt.Type, evt.Day, source, to,
                    world.Day, world.Day + travelDays.Value * 2,
                    Math.Max(0.55, SimulationMath.Round(0.96 - travelDays.Value * 0.025, 1000)), "courier",
                    world.Cities[source].KnowledgeState.KnownSettlements?.GetValueOrDefault(source) is { } place
                        ? place with { ObservedDay = evt.Day } : null));
                world.Information.NextReportId++;
            }
        }
        world.Information.LastJournalIndex = world.Journal.Count;
        world.Information.Reports = world.Information.Reports.OrderBy(report => report.Id, StringComparer.Ordinal).ToList();
    }

    private static int? ShortestTravelDays(List<RouteState> routes, string from, string to)
    {
        var best = new Dictionary<string, (int Days, string Key)>(StringComparer.Ordinal) { [from] = (0, "") };
        var pending = new HashSet<string>(StringComparer.Ordinal) { from };
        while (pending.Count > 0)
        {
            var current = pending.OrderBy(id => best[id].Days).ThenBy(id => best[id].Key, StringComparer.Ordinal)
                .ThenBy(id => id, StringComparer.Ordinal).First();
            pending.Remove(current); if (current == to) return best[current].Days;
            var neighbors = routes.Where(route => route.A == current || route.B == current)
                .Select(route => (CityId: route.A == current ? route.B : route.A, Route: route))
                .OrderBy(item => item.Route.Id, StringComparer.Ordinal);
            foreach (var neighbor in neighbors)
            {
                var candidate = (Days: best[current].Days + neighbor.Route.TravelDays, Key: $"{best[current].Key}/{neighbor.Route.Id}");
                if (!best.TryGetValue(neighbor.CityId, out var known) || candidate.Days < known.Days ||
                    candidate.Days == known.Days && StringComparer.Ordinal.Compare(candidate.Key, known.Key) < 0)
                { best[neighbor.CityId] = candidate; pending.Add(neighbor.CityId); }
            }
        }
        return null;
    }
}
