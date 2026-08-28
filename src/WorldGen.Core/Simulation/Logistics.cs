using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class Logistics
{
    private const double Epsilon = 1e-9;

    public static void Deliver(WorldState world, DailyTelemetry? telemetry = null)
    {
        var arrived = world.Shipments.Where(shipment => shipment.ArrivalDay <= world.Day).OrderBy(shipment => shipment.Id, StringComparer.Ordinal).ToArray();
        var ids = arrived.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        world.Shipments = world.Shipments.Where(shipment => !ids.Contains(shipment.Id)).ToList();
        foreach (var shipment in arrived)
        {
            var city = world.Cities[shipment.To];
            city.Stocks[shipment.ResourceId] = SimulationMath.Quantize(city.Stocks[shipment.ResourceId] + shipment.Amount);
            Journal.Record(world, "shipment_arrived", shipment.Id, [shipment.DispatchEventId],
                new JsonObject { ["from"] = shipment.From, ["to"] = shipment.To, ["resourceId"] = shipment.ResourceId, ["amount"] = shipment.Amount });
            if (telemetry is not null) telemetry.ShipmentsArrived++;
        }
    }

    public static void Plan(WorldState world, ContentCatalog content, DailyTelemetry telemetry)
    {
        world.TradeIntents = world.TradeIntents.Where(intent => intent.Remaining > Epsilon && intent.ExpiresDay > world.Day).ToList();
        Publish(world, content);
        Match(world, content, telemetry);
        world.TradeIntents = world.TradeIntents.Where(intent => intent.Remaining > Epsilon && intent.ExpiresDay > world.Day)
            .OrderBy(intent => intent.Id, StringComparer.Ordinal).ToList();
    }

    private static void Publish(WorldState world, ContentCatalog content)
    {
        if (world.Day % 3 != 0) return;
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        foreach (var resourceId in content.Resources.Resources.Select(resource => resource.Id).Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId];
            var target = NeedsAndDemand.ResourceTargetStock(world, city, resourceId, content);
            var incoming = world.Shipments.Where(item => item.To == cityId && item.ResourceId == resourceId).Sum(item => item.Amount);
            var effective = city.Stocks[resourceId] + incoming;
            var demand = target - effective - Outstanding(world, cityId, resourceId, "demand");
            if (target > Epsilon && demand > Epsilon) CreateIntent(world, "demand", city, resourceId, demand);
            var offer = city.Stocks[resourceId] - target * 1.15 - Outstanding(world, cityId, resourceId, "offer");
            if (offer > Epsilon) CreateIntent(world, "offer", city, resourceId, offer);
        }
    }

    private static double Outstanding(WorldState world, string cityId, string resourceId, string kind) => world.TradeIntents
        .Where(intent => intent.CityId == cityId && intent.ResourceId == resourceId && intent.Kind == kind).Sum(intent => intent.Remaining);

    private static void CreateIntent(WorldState world, string kind, CityState city, string resourceId, double amount)
    {
        world.TradeIntents.Add(new TradeIntentState
        {
            Id = $"intent-{world.NextTradeIntentId:000000}", Kind = kind, CityId = city.Id, ResourceId = resourceId,
            Amount = SimulationMath.Quantize(amount), Remaining = SimulationMath.Quantize(amount), CreatedDay = world.Day,
            AvailableDay = world.Day + 1, ExpiresDay = world.Day + 12,
            LimitPrice = SimulationMath.Round(city.Markets[resourceId].Price * (kind == "demand" ? 1.18 : 0.9), 10_000)
        });
        world.NextTradeIntentId++;
    }

    private static void Match(WorldState world, ContentCatalog content, DailyTelemetry telemetry)
    {
        var recipes = content.Recipes.Recipes.ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        var capacity = world.Routes.ToDictionary(route => route.Id, route => route.DailyCapacity, StringComparer.Ordinal);
        var demands = world.TradeIntents.Where(intent => intent.Kind == "demand" && intent.AvailableDay <= world.Day && intent.Remaining > Epsilon)
            .OrderBy(intent => intent.ResourceId, StringComparer.Ordinal).ThenBy(intent => intent.CreatedDay).ThenBy(intent => intent.Id, StringComparer.Ordinal).ToArray();
        foreach (var demand in demands)
        {
            var offers = world.TradeIntents.Where(intent => intent.Kind == "offer" && intent.ResourceId == demand.ResourceId &&
                    intent.CityId != demand.CityId && intent.AvailableDay <= world.Day && intent.Remaining > Epsilon)
                .Select(offer => (Offer: offer, Path: ShortestPath(world.Routes, offer.CityId, demand.CityId, capacity)))
                .Where(candidate => candidate.Path is not null && demand.LimitPrice >= candidate.Offer.LimitPrice * (1 + candidate.Path.Distance * 0.015))
                .OrderBy(candidate => candidate.Path!.Distance).ThenBy(candidate => candidate.Path!.Key, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Offer.Id, StringComparer.Ordinal).ToArray();
            foreach (var candidate in offers)
            {
                if (demand.Remaining <= Epsilon) break;
                var offer = candidate.Offer; var path = candidate.Path!; var source = world.Cities[offer.CityId];
                var target = NeedsAndDemand.ResourceTargetStock(world, source, demand.ResourceId, content);
                var surplus = Math.Max(0, source.Stocks[demand.ResourceId] - target * 1.05);
                var pathCapacity = path.RouteIds.Min(id => capacity[id]);
                var amount = SimulationMath.Quantize(Math.Min(Math.Min(demand.Remaining, offer.Remaining), Math.Min(surplus, pathCapacity)));
                if (amount <= Epsilon) continue;
                source.Stocks[demand.ResourceId] = SimulationMath.Quantize(source.Stocks[demand.ResourceId] - amount);
                demand.Remaining = SimulationMath.Quantize(demand.Remaining - amount); offer.Remaining = SimulationMath.Quantize(offer.Remaining - amount);
                foreach (var routeId in path.RouteIds) capacity[routeId] = SimulationMath.Quantize(capacity[routeId] - amount);
                var shipmentId = $"shipment-{world.NextShipmentId:000000}"; world.NextShipmentId++;
                var evt = Journal.Record(world, "shipment_dispatched", shipmentId,
                    source.ResourceSignals.TryGetValue(demand.ResourceId, out var cause) ? [cause] : [],
                    new JsonObject { ["from"] = offer.CityId, ["to"] = demand.CityId, ["resourceId"] = demand.ResourceId,
                        ["amount"] = amount, ["routeIds"] = JsonSerializer.SerializeToNode(path.RouteIds), ["offerIntentId"] = offer.Id,
                        ["demandIntentId"] = demand.Id, ["unitPrice"] = SimulationMath.Round((offer.LimitPrice + demand.LimitPrice) / 2, 10_000),
                        ["arrivalDay"] = world.Day + path.Distance });
                world.Shipments.Add(new ShipmentState(shipmentId, offer.CityId, demand.CityId, demand.ResourceId, amount,
                    path.RouteIds, world.Day, world.Day + path.Distance, evt.Id));
                telemetry.ShipmentsDispatched++;
            }
            if (demand.Remaining > Epsilon && demand.ShortfallEventId is null)
            {
                var causes = world.Cities.Values.SelectMany(city => city.Industries
                    .Where(industry => recipes[industry.RecipeId].Outputs.ContainsKey(demand.ResourceId))
                    .Select(industry => industry.ConstraintEventId)).Where(id => id is not null);
                var evt = Journal.Record(world, "trade_shortfall", demand.Id, causes,
                    new JsonObject { ["cityId"] = demand.CityId, ["resourceId"] = demand.ResourceId, ["missingAmount"] = demand.Remaining });
                demand.ShortfallEventId = evt.Id; world.Cities[demand.CityId].ResourceSignals[demand.ResourceId] = evt.Id;
            }
        }
        world.Shipments = world.Shipments.OrderBy(shipment => shipment.Id, StringComparer.Ordinal).ToList();
    }

    private static PathState? ShortestPath(List<RouteState> routes, string from, string to, Dictionary<string, double> capacity)
    {
        var adjacency = new Dictionary<string, List<(string CityId, RouteState Route)>>(StringComparer.Ordinal);
        foreach (var route in routes.Where(route => capacity.GetValueOrDefault(route.Id) > Epsilon))
        {
            if (!adjacency.TryGetValue(route.A, out var a)) adjacency[route.A] = a = [];
            if (!adjacency.TryGetValue(route.B, out var b)) adjacency[route.B] = b = [];
            a.Add((route.B, route)); b.Add((route.A, route));
        }
        foreach (var list in adjacency.Values) list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Route.Id, b.Route.Id));
        var best = new Dictionary<string, PathState>(StringComparer.Ordinal) { [from] = new(0, "", []) };
        var pending = new HashSet<string>(StringComparer.Ordinal) { from };
        while (pending.Count > 0)
        {
            var current = pending.OrderBy(id => best[id].Distance).ThenBy(id => best[id].Key, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).First();
            pending.Remove(current); if (current == to) return best[current];
            if (!adjacency.TryGetValue(current, out var edges)) continue;
            foreach (var edge in edges)
            {
                var known = best.GetValueOrDefault(edge.CityId); var currentBest = best[current];
                var candidate = new PathState(currentBest.Distance + edge.Route.TravelDays, $"{currentBest.Key}/{edge.Route.Id}", [.. currentBest.RouteIds, edge.Route.Id]);
                if (known is null || candidate.Distance < known.Distance || candidate.Distance == known.Distance && StringComparer.Ordinal.Compare(candidate.Key, known.Key) < 0)
                { best[edge.CityId] = candidate; pending.Add(edge.CityId); }
            }
        }
        return null;
    }

    private sealed record PathState(int Distance, string Key, List<string> RouteIds);
}
