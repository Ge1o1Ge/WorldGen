using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public static class EndogenousEvents
{
    public static void Advance(WorldState world)
    {
        if (world.Day == 0 || world.Day % 30 != 0) return;
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; if (city.ActiveEffects.Count > 0) continue;
            var node = world.Spatial.Nodes[city.SpatialNodeId];
            var wetland = node.Aggregate.BiomeShares.GetValueOrDefault("wetland") + node.Aggregate.BiomeShares.GetValueOrDefault("floodplain") * 0.35;
            var density = node.Aggregate.Population / (double)Math.Max(1, node.ChildTerritoryIds!.Count);
            var arrivals = world.Journal.Where(evt => evt.Day >= world.Day - 30 && evt.Type == "shipment_arrived" &&
                evt.Details["to"]?.GetValue<string>() == cityId).ToArray();
            var traffic = arrivals.Length;
            var risk = 0.0008 + wetland * 0.003 + Math.Min(0.0015, density / 25_000) +
                (1 - city.Demography.Health) * 0.006 + Math.Min(0.001, traffic * 0.00005);
            var random = world.RandomStreams["events"];
            if (random.NextDouble() >= risk) continue;
            var duration = 24 + (int)Math.Floor(random.NextDouble() * 43);
            var multiplier = 0.58 + random.NextDouble() * 0.2;
            var territoryId = SpatialRuntime.LocateEventTerritory(world, cityId, random);
            var causes = arrivals.TakeLast(3).Select(evt => evt.Id);
            var effectId = $"endemic-fever:{cityId}:{world.Day}";
            var label = $"Лихорадка в поселении «{city.Name}»";
            var evt = Journal.Record(world, "crisis_started", effectId, causes,
                new JsonObject { ["cityId"] = cityId, ["territoryId"] = territoryId, ["label"] = label,
                    ["multiplier"] = multiplier, ["durationDays"] = duration, ["endogenous"] = true,
                    ["emergence"] = new JsonObject { ["wetlandShare"] = wetland, ["density"] = density,
                        ["health"] = city.Demography.Health, ["traffic"] = traffic, ["risk"] = risk } });
            city.ActiveEffects[effectId] = new ActiveEffectState(multiplier, world.Day + duration, evt.Id, territoryId, label, true);
            SpatialRuntime.ActivateCityDetail(world, cityId, evt.Id, duration + world.LodPolicy.CrisisCooldownDays);
            SpatialRuntime.ActivateTerritoryDetail(world, territoryId, evt.Id, duration + world.LodPolicy.CrisisCooldownDays);
        }
    }

    public static void EndExpiredEffects(WorldState world)
    {
        foreach (var city in world.Cities.Values)
        foreach (var pair in city.ActiveEffects.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
        {
            if (pair.Value.EndDay != world.Day) continue;
            Journal.Record(world, "crisis_ended", pair.Key, [pair.Value.StartEventId],
                new JsonObject { ["cityId"] = city.Id, ["label"] = pair.Value.Label, ["endogenous"] = pair.Value.Endogenous });
            city.ActiveEffects.Remove(pair.Key);
        }
    }
}
