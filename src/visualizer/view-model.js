import { stateHash } from "../core/canonical-json.js";
import { snapshotWorld } from "../simulation/world.js";

function rounded(value) {
  return Math.round(value * 100) / 100;
}

const BIOMES = ["river", "wetland", "floodplain", "forest", "upland_forest", "meadow", "dry_grassland"];

export function buildVisualizationBootstrap(world, content) {
  const cities = Object.values(world.cities);
  const cityIndex = new Map(cities.map((city, index) => [city.id, index]));
  const biomeIndex = new Map(BIOMES.map((biome, index) => [biome, index]));

  return {
    scenarioName: content.scenario.name,
    mapName: content.map.name,
    grid: world.spatial.grid,
    biomes: BIOMES,
    resourceNames: Object.fromEntries(content.resources.resources.map((resource) => [resource.id, resource.name])),
    recipeNames: Object.fromEntries(content.recipes.recipes.map((recipe) => [recipe.id, recipe.name])),
    technologyNames: Object.fromEntries(content.technologies.technologies.map((technology) => [technology.id, technology.name])),
    cities: cities.map((city) => {
      const definition = content.scenario.cities.find((item) => item.id === city.id);
      return { id: city.id, name: city.name, anchor: definition.anchor };
    }),
    // Compact tuples keep the one-time 10 000-zone payload reasonably small:
    // [x, y, city, population, fertility, biome, diagonal, elevation, moisture,
    //  forest, arable, pasture, timber, fish, clay, stone, iron].
    zones: Object.values(world.spatial.territories).map((territory) => [
      territory.grid.x,
      territory.grid.y,
      cityIndex.get(territory.assignedCityId),
      territory.population,
      rounded(territory.fertility),
      biomeIndex.get(territory.biome),
      territory.diagonal === "nw-se" ? 0 : 1,
      territory.elevationMeters,
      territory.moisture,
      territory.forestCover,
      territory.resourcePotential.arable,
      territory.resourcePotential.pasture,
      territory.resourcePotential.timber,
      territory.resourcePotential.fish,
      territory.resourcePotential.clay,
      territory.resourcePotential.stone,
      territory.resourcePotential.iron_ore
    ]),
    macros: Object.values(world.spatial.nodes)
      .filter((node) => node.kind === "macro")
      .map((node) => {
        const dominantBiome = Object.entries(node.aggregate.biomeShares)
          .sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]))[0][0];
        return [
          node.grid.x, node.grid.y, cityIndex.get(node.dominantCityId),
          rounded(node.aggregate.meanElevationMeters), rounded(node.aggregate.meanMoisture),
          biomeIndex.get(dominantBiome),
          ...["arable", "pasture", "timber", "fish", "clay", "stone", "iron_ore"]
            .map((id) => rounded(node.aggregate.resourcePotential[id]))
        ];
      }),
    routes: world.routes
  };
}

export function buildVisualizationState(world) {
  const activeCrisisZoneIds = Object.values(world.cities).flatMap((city) =>
    Object.values(city.activeEffects).map((effect) => effect.territoryId)
  );
  const cities = Object.values(world.cities).map((city) => {
    const spatialNode = world.spatial.nodes[city.spatialNodeId];
    return {
      id: city.id,
      name: city.name,
      population: spatialNode.aggregate.population,
      detailed: spatialNode.detail !== null,
      activeUntilDay: spatialNode.activeUntilDay,
      food: rounded(city.stocks.food),
      grain: rounded(city.stocks.grain),
      shortageActive: city.shortage.active,
      shortageDays: city.shortage.days,
      missingFood: rounded(city.shortage.totalFoodMissing),
      health: rounded(city.demography.health),
      localReserveDays: city.localReserveDays,
      constrainedIndustries: city.industries.filter((industry) => industry.lastConstraintKey !== null).length,
      markets: {
        food: { price: rounded(city.markets.food.price), coverageDays: city.markets.food.coverageDays },
        firewood: { price: rounded(city.markets.firewood.price), coverageDays: city.markets.firewood.coverageDays }
      },
      technologies: Object.entries(city.technologyState)
        .map(([id, technology]) => ({ id, adoption: rounded(technology.adoption), knowledge: rounded(technology.knowledge) }))
        .sort((left, right) => right.adoption - left.adoption || left.id.localeCompare(right.id))
        .slice(0, 4)
    };
  });

  const importantEventTypes = new Set([
    "crisis_started",
    "crisis_ended",
    "food_shortage_started",
    "food_shortage_ended",
    "spatial_node_expanded",
    "spatial_node_collapsed",
    "actor_became_significant",
    "technology_milestone",
    "migration_flow",
    "institution_decision",
    "resource_shortage_started",
    "resource_shortage_ended",
    "price_shock_started",
    "price_shock_ended",
    "infrastructure_degraded",
    "information_received"
  ]);
  const lastCompletedDay = world.day - 1;
  const journalOperations = world.journal.filter((event) =>
    event.day === lastCompletedDay && [
      "shipment_dispatched", "shipment_arrived", "production_constrained"
    ].includes(event.type)
  ).length;
  const routineOperations = Object.keys(world.cities).length +
    Object.values(world.cities).reduce((sum, city) => sum + city.industries.length, 0);

  const latestTelemetry = world.telemetry.daily.at(-1) ?? null;
  const detailedMacroIds = Object.values(world.spatial.nodes)
    .filter((node) => node.kind === "macro" && node.detail !== null)
    .map((node) => node.id)
    .sort();
  const averageRoadCondition = world.routes.reduce((sum, route) => sum + route.condition, 0) /
    Math.max(1, world.routes.length);
  return {
    day: world.day,
    hash: stateHash(snapshotWorld(world)),
    stats: {
      activeNodes: cities.filter((city) => city.detailed).length + detailedMacroIds.length,
      shortageCities: cities.filter((city) => city.shortageActive).length,
      shipments: world.shipments.length,
      actors: Object.keys(world.actors).length,
      operationsLastDay: world.day === 0 ? 0 : routineOperations + journalOperations,
      population: cities.reduce((sum, city) => sum + city.population, 0),
      tradeIntents: world.tradeIntents.length,
      knowledgeTransfers: world.knowledgeTransfers.length,
      reports: world.information.reports.length,
      averageRoadCondition: rounded(averageRoadCondition)
    },
    latestTelemetry,
    crisisZoneIds: activeCrisisZoneIds,
    detailedMacroIds,
    environmentalSites: Object.values(world.cities).flatMap((city) => city.industries.map((industry) => {
      const territory = world.spatial.territories[industry.zoneId];
      return {
        industryId: industry.id,
        recipeId: industry.recipeId,
        cityId: city.id,
        zone: territory.grid,
        naturalState: {
          soilQuality: rounded(territory.naturalState.soilQuality),
          forestBiomass: rounded(territory.naturalState.forestBiomass),
          fishStock: rounded(territory.naturalState.fishStock),
          deposits: Object.fromEntries(Object.entries(territory.naturalState.deposits)
            .map(([id, value]) => [id, rounded(value)]))
        }
      };
    })),
    routeStates: world.routes.map((route) => ({
      id: route.id,
      travelDays: route.travelDays,
      dailyCapacity: rounded(route.dailyCapacity),
      condition: rounded(route.condition)
    })),
    cities,
    shipments: world.shipments.map((shipment) => ({
      id: shipment.id,
      from: shipment.from,
      to: shipment.to,
      resourceId: shipment.resourceId,
      amount: rounded(shipment.amount),
      departureDay: shipment.departureDay,
      arrivalDay: shipment.arrivalDay,
      progress: Math.max(0, Math.min(1,
        (world.day - shipment.departureDay) / (shipment.arrivalDay - shipment.departureDay)
      ))
    })),
    actors: Object.values(world.actors).map((actor) => ({
      id: actor.id,
      name: actor.name,
      role: actor.role,
      zone: world.spatial.territories[actor.location.territoryId].grid,
      importance: actor.importance.score
    })),
    recentEvents: world.journal
      .filter((event) => importantEventTypes.has(event.type))
      .slice(-12)
      .reverse()
  };
}
