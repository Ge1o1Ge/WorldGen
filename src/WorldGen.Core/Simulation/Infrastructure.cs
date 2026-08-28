using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public static class Infrastructure
{
    public static void Advance(WorldState world, DailyTelemetry telemetry)
    {
        if (world.Day == 0 || world.Day % 30 != 0) return;
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            var ratios = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var resourceId in NeedsAndDemand.InfrastructureResourceIds)
            {
                var needed = NeedsAndDemand.InfrastructureMonthlyNeed(city, population, resourceId);
                var consumed = Math.Min(needed, city.Stocks[resourceId]);
                city.Stocks[resourceId] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resourceId] - consumed));
                ratios[resourceId] = needed > 0 ? consumed / needed : 1;
                telemetry.InfrastructureConsumptionByResource[resourceId] = SimulationMath.Quantize(
                    telemetry.InfrastructureConsumptionByResource.GetValueOrDefault(resourceId) + consumed);
            }
            var housingSupply = Math.Min(ratios["timber"], ratios["clay"]);
            var roadSupply = Math.Min(ratios["stone"], ratios["tools"]);
            var previous = city.Infrastructure.HousingCondition;
            city.Infrastructure.HousingCondition = SimulationMath.Clamp(previous + (housingSupply - 0.9) * 0.012, 0, 1);
            city.Infrastructure.RoadCondition = SimulationMath.Clamp(city.Infrastructure.RoadCondition + (roadSupply - 0.88) * 0.009, 0, 1);
            city.Infrastructure.Sanitation = SimulationMath.Clamp(city.Infrastructure.Sanitation +
                (Math.Min(ratios["clay"], city.Shortage.Active ? 0.25 : 1) - 0.9) * 0.006, 0, 1);
            if (previous >= 0.5 && city.Infrastructure.HousingCondition < 0.5)
            {
                var causes = ratios.Where(pair => pair.Value < 0.5)
                    .Select(pair => city.ResourceSignals.GetValueOrDefault(pair.Key)).Where(id => id is not null);
                Journal.Record(world, "infrastructure_degraded", cityId, causes,
                    new JsonObject { ["cityId"] = cityId, ["component"] = "housing", ["condition"] = city.Infrastructure.HousingCondition,
                        ["supplyRatios"] = JsonSerializer.SerializeToNode(ratios) });
            }
        }

        foreach (var route in world.Routes.OrderBy(route => route.Id, StringComparer.Ordinal))
        {
            var endpoint = (world.Cities[route.A].Infrastructure.RoadCondition + world.Cities[route.B].Infrastructure.RoadCondition) / 2;
            var traffic = world.Journal.Where(evt => evt.Day >= world.Day - 30 && evt.Type == "shipment_dispatched" &&
                evt.Details["routeIds"]!.AsArray().Any(node => node!.GetValue<string>() == route.Id))
                .Sum(evt => evt.Details["amount"]!.GetValue<double>());
            var utilization = traffic / Math.Max(1, route.BaseDailyCapacity * 30);
            var previous = route.Condition;
            route.Condition = SimulationMath.Clamp(route.Condition + (endpoint - route.Condition) * 0.035 - Math.Min(0.006, utilization * 0.003), 0, 1);
            route.TravelDays = Math.Max(1, (int)SimulationMath.Round(route.BaseTravelDays * (1.32 - route.Condition * 0.32), 1));
            route.DailyCapacity = SimulationMath.Quantize(route.BaseDailyCapacity * (0.55 + route.Condition * 0.45));
            if (previous >= 0.4 && route.Condition < 0.4)
                Journal.Record(world, "infrastructure_degraded", route.Id, details:
                    new JsonObject { ["component"] = "route", ["routeId"] = route.Id, ["condition"] = route.Condition, ["utilization"] = utilization });
        }
    }
}
