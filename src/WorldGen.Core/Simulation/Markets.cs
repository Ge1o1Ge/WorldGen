using System.Text.Json.Nodes;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class Markets
{
    public static void Advance(WorldState world, ContentCatalog content)
    {
        if (world.Day % 7 != 0) return;
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        foreach (var resource in content.Resources.Resources.OrderBy(resource => resource.Id, StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; var market = city.Markets[resource.Id];
            var target = NeedsAndDemand.ResourceTargetStock(world, city, resource.Id, content);
            var effective = city.Stocks[resource.Id] + world.Shipments
                .Where(shipment => shipment.To == cityId && shipment.ResourceId == resource.Id).Sum(shipment => shipment.Amount);
            var dailyNeed = NeedsAndDemand.DailyResourceNeed(world, city, resource.Id, content);
            var scarcity = target > 0
                ? SimulationMath.Clamp((target + 1) / (effective + 1), 0.08, 12)
                : SimulationMath.Clamp(1 / (1 + effective / 250), 0.35, 1);
            var rawPrice = resource.BaseValue * SimulationMath.Clamp(Math.Pow(scarcity, 0.62), 0.3, 4.5);
            var previous = market.Price;
            market.Price = SimulationMath.Round(previous * 0.72 + rawPrice * 0.28, 10_000);
            market.TargetStock = SimulationMath.Round(target, 1000);
            market.CoverageDays = dailyNeed > 0 ? SimulationMath.Round(effective / dailyNeed, 10) : null;
            market.Availability = target > 0 ? SimulationMath.Clamp(effective / target, 0, 2) : 1;
            var wasShock = market.ShockActive;
            market.ShockActive = market.Price >= resource.BaseValue * 2.5;
            if (market.ShockActive && !wasShock)
            {
                var evt = Journal.Record(world, "price_shock_started", $"{cityId}:{resource.Id}",
                    city.ResourceSignals.TryGetValue(resource.Id, out var cause) ? [cause] : [],
                    new JsonObject { ["cityId"] = cityId, ["resourceId"] = resource.Id, ["price"] = market.Price, ["baseValue"] = resource.BaseValue });
                market.ShockEventId = evt.Id;
            }
            else if (!market.ShockActive && wasShock)
            {
                Journal.Record(world, "price_shock_ended", $"{cityId}:{resource.Id}",
                    market.ShockEventId is null ? [] : [market.ShockEventId],
                    new JsonObject { ["cityId"] = cityId, ["resourceId"] = resource.Id, ["price"] = market.Price });
                market.ShockEventId = null;
            }
        }
    }
}
