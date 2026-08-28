using WorldGen.Core.Content;
using WorldGen.Core.Simulation;

namespace WorldGen.Server;

internal static class VisualizationViewModel
{
    private static readonly string[] Biomes = ["river", "wetland", "floodplain", "forest", "upland_forest", "meadow", "dry_grassland"];
    private static readonly string[] Potentials = ["arable", "pasture", "timber", "fish", "clay", "stone", "iron_ore"];
    private static readonly HashSet<string> ImportantEventTypes = new(StringComparer.Ordinal)
    {
        "crisis_started", "crisis_ended", "food_shortage_started", "food_shortage_ended",
        "spatial_node_expanded", "spatial_node_collapsed", "actor_became_significant", "technology_milestone",
        "migration_flow", "institution_decision", "resource_shortage_started", "resource_shortage_ended",
        "price_shock_started", "price_shock_ended", "infrastructure_degraded", "information_received"
    };

    public static object Bootstrap(WorldState world, ContentCatalog content)
    {
        var cities = world.Cities.Values.ToArray();
        var cityIndex = cities.Select((city, index) => (city.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var biomeIndex = Biomes.Select((biome, index) => (biome, index)).ToDictionary(item => item.biome, item => item.index, StringComparer.Ordinal);
        return new
        {
            scenarioName = content.Scenario.Name,
            mapName = content.Map.Name,
            grid = world.Spatial.Grid,
            biomes = Biomes,
            resourceNames = content.Resources.Resources.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal),
            recipeNames = content.Recipes.Recipes.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal),
            technologyNames = content.Technologies.Technologies.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal),
            cities = cities.Select(city => new { city.Id, city.Name, anchor = content.Scenario.Cities.First(item => item.Id == city.Id).Anchor }),
            zones = world.Spatial.Territories.Values.Select(territory => new object[]
            {
                territory.Grid.X, territory.Grid.Y, cityIndex[territory.AssignedCityId], territory.Population, Round(territory.Fertility),
                biomeIndex[territory.Biome], territory.Diagonal == "nw-se" ? 0 : 1, territory.ElevationMeters, territory.Moisture,
                territory.ForestCover, territory.ResourcePotential["arable"], territory.ResourcePotential["pasture"],
                territory.ResourcePotential["timber"], territory.ResourcePotential["fish"], territory.ResourcePotential["clay"],
                territory.ResourcePotential["stone"], territory.ResourcePotential["iron_ore"]
            }),
            macros = world.Spatial.Nodes.Values.Where(node => node.Kind == "macro").Select(node =>
            {
                var dominantBiome = node.Aggregate.BiomeShares.OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.InvariantCulture).First().Key;
                return new object[] { node.Grid!.X, node.Grid.Y, cityIndex[node.DominantCityId!], Round(node.Aggregate.MeanElevationMeters),
                    Round(node.Aggregate.MeanMoisture), biomeIndex[dominantBiome] }
                    .Concat(Potentials.Select(id => (object)Round(node.Aggregate.ResourcePotential[id]))).ToArray();
            }),
            routes = world.Routes
        };
    }

    public static object State(WorldState world)
    {
        var crisisZoneIds = world.Cities.Values.SelectMany(city => city.ActiveEffects.Values.Select(effect => effect.TerritoryId)).ToArray();
        var cities = world.Cities.Values.Select(city =>
        {
            var node = world.Spatial.Nodes[city.SpatialNodeId];
            return new
            {
                city.Id, city.Name, population = node.Aggregate.Population, detailed = node.Detail is not null,
                activeUntilDay = node.ActiveUntilDay, food = Round(city.Stocks["food"]), grain = Round(city.Stocks["grain"]),
                shortageActive = city.Shortage.Active, shortageDays = city.Shortage.Days, missingFood = Round(city.Shortage.TotalFoodMissing),
                health = Round(city.Demography.Health), localReserveDays = city.LocalReserveDays,
                constrainedIndustries = city.Industries.Count(industry => industry.LastConstraintKey is not null),
                markets = new
                {
                    food = new { price = Round(city.Markets["food"].Price), coverageDays = city.Markets["food"].CoverageDays },
                    firewood = new { price = Round(city.Markets["firewood"].Price), coverageDays = city.Markets["firewood"].CoverageDays }
                },
                technologies = city.TechnologyState.Select(pair => new { id = pair.Key, adoption = Round(pair.Value.Adoption), knowledge = Round(pair.Value.Knowledge) })
                    .OrderByDescending(item => item.adoption).ThenBy(item => item.id, StringComparer.InvariantCulture).Take(4)
            };
        }).ToArray();
        var lastDay = world.Day - 1;
        var journalOperations = world.Journal.Count(evt => evt.Day == lastDay && evt.Type is "shipment_dispatched" or "shipment_arrived" or "production_constrained");
        var routine = world.Cities.Count + world.Cities.Values.Sum(city => city.Industries.Count);
        var detailedMacroIds = world.Spatial.Nodes.Values.Where(node => node.Kind == "macro" && node.Detail is not null)
            .Select(node => node.Id).Order(StringComparer.Ordinal).ToArray();
        var averageRoad = world.Routes.Sum(route => route.Condition) / Math.Max(1, world.Routes.Count);
        return new
        {
            day = world.Day,
            hash = WorldSnapshot.Hash(world),
            stats = new
            {
                activeNodes = cities.Count(city => city.detailed) + detailedMacroIds.Length,
                shortageCities = cities.Count(city => city.shortageActive), shipments = world.Shipments.Count, actors = world.Actors.Count,
                operationsLastDay = world.Day == 0 ? 0 : routine + journalOperations,
                population = cities.Sum(city => city.population), tradeIntents = world.TradeIntents.Count,
                knowledgeTransfers = world.KnowledgeTransfers.Count, reports = world.Information.Reports.Count,
                averageRoadCondition = Round(averageRoad)
            },
            latestTelemetry = world.Telemetry.Daily.LastOrDefault(), crisisZoneIds, detailedMacroIds,
            environmentalSites = world.Cities.Values.SelectMany(city => city.Industries.Select(industry =>
            {
                var territory = world.Spatial.Territories[industry.ZoneId];
                return new
                {
                    industryId = industry.Id, recipeId = industry.RecipeId, cityId = city.Id, zone = territory.Grid,
                    naturalState = new { soilQuality = Round(territory.NaturalState.SoilQuality), forestBiomass = Round(territory.NaturalState.ForestBiomass),
                        fishStock = Round(territory.NaturalState.FishStock), deposits = territory.NaturalState.Deposits.ToDictionary(pair => pair.Key, pair => Round(pair.Value), StringComparer.Ordinal) }
                };
            })),
            routeStates = world.Routes.Select(route => new { route.Id, route.TravelDays, dailyCapacity = Round(route.DailyCapacity), condition = Round(route.Condition) }),
            cities,
            shipments = world.Shipments.Select(shipment => new { shipment.Id, shipment.From, shipment.To, shipment.ResourceId,
                amount = Round(shipment.Amount), shipment.DepartureDay, shipment.ArrivalDay,
                progress = Math.Max(0, Math.Min(1, (world.Day - shipment.DepartureDay) / (double)(shipment.ArrivalDay - shipment.DepartureDay))) }),
            actors = world.Actors.Values.Select(actor => new { actor.Id, actor.Name, actor.Role,
                zone = world.Spatial.Territories[actor.Location.TerritoryId].Grid, importance = actor.Importance.Score }),
            recentEvents = world.Journal.Where(evt => ImportantEventTypes.Contains(evt.Type)).TakeLast(12).Reverse()
        };
    }

    private static double Round(double value) => Math.Floor(value * 100 + 0.5) / 100;
}
