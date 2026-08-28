using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public static class ScheduledEvents
{
    public static void Begin(WorldState world)
    {
        foreach (var scheduled in world.ScheduledEvents)
        {
            if (scheduled.StartDay != world.Day) continue;
            var territoryId = SpatialRuntime.LocateEventTerritory(world, scheduled.CityId, world.RandomStreams["events"]);
            var evt = Journal.Record(world, "crisis_started", scheduled.Id, details:
                new JsonObject { ["cityId"] = scheduled.CityId, ["territoryId"] = territoryId, ["label"] = scheduled.Label,
                    ["multiplier"] = scheduled.Multiplier, ["durationDays"] = scheduled.DurationDays, ["endogenous"] = false });
            world.Cities[scheduled.CityId].ActiveEffects[scheduled.Id] = new ActiveEffectState(
                scheduled.Multiplier, scheduled.StartDay + scheduled.DurationDays, evt.Id, territoryId, scheduled.Label, false);
            var activeDays = scheduled.DurationDays + world.LodPolicy.CrisisCooldownDays;
            SpatialRuntime.ActivateCityDetail(world, scheduled.CityId, evt.Id, activeDays);
            SpatialRuntime.ActivateTerritoryDetail(world, territoryId, evt.Id, activeDays);
        }
    }
}
