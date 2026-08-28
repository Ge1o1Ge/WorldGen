using System.Text.Json.Nodes;
using WorldGen.Core.Spatial;

namespace WorldGen.Core.Simulation;

public static class SpatialRuntime
{
    public static void RecalculateAggregates(WorldState world)
    {
        foreach (var node in world.Spatial.Nodes.Values.Where(node => node.Kind == "macro"))
        {
            var territories = node.ChildTerritoryIds!.Select(id => world.Spatial.Territories[id]).ToArray();
            node.Aggregate = SpatialGenerator.AggregateTerritories(territories);
            node.DominantCityId = SpatialGenerator.DominantCity(territories);
        }
        foreach (var node in world.Spatial.Nodes.Values.Where(node => node.Kind == "city"))
        {
            node.Aggregate = SpatialGenerator.AggregateTerritories(node.ChildTerritoryIds!.Select(id => world.Spatial.Territories[id]));
        }
        world.Spatial.Nodes[world.Spatial.RegionNodeId].Aggregate =
            SpatialGenerator.AggregateTerritories(world.Spatial.Territories.Values);
    }

    public static SpatialNode ActivateCityDetail(WorldState world, string cityId, string? causeEventId = null, int keepActiveDays = 1)
    {
        var node = world.Spatial.Nodes[SpatialGenerator.CitySpatialNodeId(cityId)];
        return Activate(world, node, causeEventId, keepActiveDays, cityId, null);
    }

    public static SpatialNode ActivateTerritoryDetail(WorldState world, string territoryId, string? causeEventId = null, int keepActiveDays = 1)
    {
        var territory = world.Spatial.Territories[territoryId];
        var node = world.Spatial.Nodes[territory.ParentNodeId];
        return Activate(world, node, causeEventId, keepActiveDays, null, territoryId);
    }

    private static SpatialNode Activate(WorldState world, SpatialNode node, string? causeEventId, int keepActiveDays, string? cityId, string? territoryId)
    {
        if (keepActiveDays < 1) throw new ArgumentOutOfRangeException(nameof(keepActiveDays));
        if (node.Detail is null)
        {
            var children = node.ChildTerritoryIds!;
            var ids = children.ToHashSet(StringComparer.Ordinal);
            var detail = new SpatialDetail
            {
                ExpandedDay = world.Day,
                TriggerEventIds = causeEventId is null ? [] : [causeEventId],
                ZoneCount = children.Count,
                ActorIds = world.Actors.Values.Where(actor => ids.Contains(actor.Location.TerritoryId))
                    .Select(actor => actor.Id).Order(StringComparer.Ordinal).ToList()
            };
            node.Detail = detail;
            var details = node.Kind == "city"
                ? new JsonObject { ["cityId"] = cityId, ["zoneCount"] = detail.ZoneCount, ["actorIds"] = JsonSerializerNode(detail.ActorIds) }
                : new JsonObject { ["kind"] = "macro", ["macroNodeId"] = node.Id, ["territoryId"] = territoryId,
                    ["zoneCount"] = detail.ZoneCount, ["actorIds"] = JsonSerializerNode(detail.ActorIds) };
            detail.ExpansionEventId = Journal.Record(world, "spatial_node_expanded", node.Id,
                causeEventId is null ? [] : [causeEventId], details).Id;
        }
        else if (causeEventId is not null && !node.Detail.TriggerEventIds.Contains(causeEventId, StringComparer.Ordinal))
        {
            node.Detail.TriggerEventIds.Add(causeEventId);
            node.Detail.TriggerEventIds.Sort(StringComparer.Ordinal);
        }
        node.ActiveUntilDay = Math.Max(node.ActiveUntilDay ?? 0, world.Day + keepActiveDays);
        return node;
    }

    public static void CollapseExpired(WorldState world)
    {
        foreach (var node in world.Spatial.Nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (node.Kind is not ("city" or "macro") || node.Detail is null || node.ActiveUntilDay > world.Day) continue;
            Journal.Record(world, "spatial_node_collapsed", node.Id,
                node.Detail.ExpansionEventId is null ? [] : [node.Detail.ExpansionEventId],
                new JsonObject
                {
                    ["kind"] = node.Kind,
                    ["cityId"] = node.Kind == "city" ? node.WorldEntityId : null,
                    ["macroNodeId"] = node.Kind == "macro" ? node.Id : null,
                    ["activeDays"] = world.Day - node.Detail.ExpandedDay
                });
            node.Detail = null;
            node.ActiveUntilDay = null;
        }
    }

    public static string LocateEventTerritory(WorldState world, string cityId, Determinism.SeededRandom random)
    {
        var node = world.Spatial.Nodes[SpatialGenerator.CitySpatialNodeId(cityId)];
        var territories = node.ChildTerritoryIds!.Select(id => world.Spatial.Territories[id]).ToArray();
        var total = territories.Sum(territory => territory.Population);
        if (total == 0) return node.AnchorTerritoryId!;
        var position = random.NextDouble() * total;
        foreach (var territory in territories)
        {
            position -= territory.Population;
            if (position <= 0) return territory.Id;
        }
        return territories[^1].Id;
    }

    private static JsonNode? JsonSerializerNode<T>(T value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
}
