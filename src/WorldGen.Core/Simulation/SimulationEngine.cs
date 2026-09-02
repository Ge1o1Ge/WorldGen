using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class SimulationEngine
{
    public static WorldState Step(WorldState world, ContentCatalog content, IReadOnlyDictionary<string, double>? utilization = null, SettlementSimulation? development = null)
    {
        SpatialRuntime.CollapseExpired(world);
        EndogenousEvents.EndExpiredEffects(world);
        ScheduledEvents.Begin(world);
        EndogenousEvents.Advance(world);
        if (development?.IsForager != true) Technology.Advance(world, content);
        Institutions.Advance(world, content);
        var telemetry = new DailyTelemetry { Day = world.Day };
        Logistics.Deliver(world, telemetry);
        telemetry = Economy.RunDay(world, content, utilization, development) with { ShipmentsArrived = telemetry.ShipmentsArrived };
        if (development?.IsForager != true) Infrastructure.Advance(world, telemetry);
        if (development?.IsForager != true)
        {
            Markets.Advance(world, content);
            Logistics.Plan(world, content, telemetry);
        }
        MaintainShortageDetail(world);
        Demography.Advance(world);
        Information.Advance(world);
        world.Telemetry.Daily.Add(telemetry);
        if (world.Telemetry.Daily.Count > 730) world.Telemetry.Daily.RemoveAt(0);
        WorldHistory.Compact(world);
        world.Day++;
        return world;
    }

    private static void MaintainShortageDetail(WorldState world)
    {
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; if (!city.Shortage.Active) continue;
            var days = Math.Max(1, world.LodPolicy.ShortageCooldownDays);
            SpatialRuntime.ActivateCityDetail(world, cityId, city.Shortage.EventId, days);
            SpatialRuntime.ActivateTerritoryDetail(world, world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!, city.Shortage.EventId, days);
        }
    }

    public static WorldState Simulate(WorldState world, ContentCatalog content, int days)
    {
        if (days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        for (var i = 0; i < days; i++) Step(world, content);
        return world;
    }
}
