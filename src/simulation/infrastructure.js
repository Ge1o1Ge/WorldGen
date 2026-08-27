import { recordEvent } from "./journal.js";

const MONTHLY_PER_PERSON = {
  timber: 0.001,
  clay: 0.0007,
  stone: 0.0005,
  tools: 0.00004
};

export function infrastructureMonthlyNeed(city, population, resourceId) {
  return population * (MONTHLY_PER_PERSON[resourceId] ?? 0) *
    (1.15 - city.infrastructure.housingCondition * 0.25);
}

function clamp01(value) {
  return Math.max(0, Math.min(1, value));
}

export function advanceInfrastructure(world, telemetry) {
  if (world.day === 0 || world.day % 30 !== 0) return;
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
    const ratios = {};
    for (const resourceId of Object.keys(MONTHLY_PER_PERSON).sort()) {
      const needed = infrastructureMonthlyNeed(city, population, resourceId);
      const consumed = Math.min(needed, city.stocks[resourceId]);
      city.stocks[resourceId] = Math.round(Math.max(0, city.stocks[resourceId] - consumed) * 1_000_000) / 1_000_000;
      ratios[resourceId] = needed > 0 ? consumed / needed : 1;
      telemetry.infrastructureConsumptionByResource[resourceId] =
        Math.round(((telemetry.infrastructureConsumptionByResource[resourceId] ?? 0) + consumed) * 1_000_000) / 1_000_000;
    }
    const housingSupply = Math.min(ratios.timber, ratios.clay);
    const roadSupply = Math.min(ratios.stone, ratios.tools);
    const previousHousing = city.infrastructure.housingCondition;
    city.infrastructure.housingCondition = clamp01(previousHousing + (housingSupply - 0.9) * 0.012);
    city.infrastructure.roadCondition = clamp01(city.infrastructure.roadCondition + (roadSupply - 0.88) * 0.009);
    city.infrastructure.sanitation = clamp01(city.infrastructure.sanitation +
      (Math.min(ratios.clay, city.shortage.active ? 0.25 : 1) - 0.9) * 0.006);

    if (previousHousing >= 0.5 && city.infrastructure.housingCondition < 0.5) {
      const causeIds = Object.entries(ratios)
        .filter(([, ratio]) => ratio < 0.5)
        .map(([resourceId]) => city.resourceSignals[resourceId])
        .filter(Boolean);
      recordEvent(world, {
        type: "infrastructure_degraded",
        subjectId: cityId,
        causeIds,
        details: { cityId, component: "housing", condition: city.infrastructure.housingCondition, supplyRatios: ratios }
      });
    }
  }

  for (const route of [...world.routes].sort((left, right) => left.id.localeCompare(right.id))) {
    const endpointCondition = (world.cities[route.a].infrastructure.roadCondition +
      world.cities[route.b].infrastructure.roadCondition) / 2;
    const traffic = world.journal
      .filter((event) => event.day >= world.day - 30 && event.type === "shipment_dispatched" &&
        event.details.routeIds.includes(route.id))
      .reduce((sum, event) => sum + event.details.amount, 0);
    const utilization = traffic / Math.max(1, route.baseDailyCapacity * 30);
    const previousCondition = route.condition;
    route.condition = clamp01(route.condition + (endpointCondition - route.condition) * 0.035 -
      Math.min(0.006, utilization * 0.003));
    route.travelDays = Math.max(1, Math.round(route.baseTravelDays * (1.32 - route.condition * 0.32)));
    route.dailyCapacity = Math.round(route.baseDailyCapacity * (0.55 + route.condition * 0.45) * 1_000_000) / 1_000_000;
    if (previousCondition >= 0.4 && route.condition < 0.4) {
      recordEvent(world, {
        type: "infrastructure_degraded",
        subjectId: route.id,
        details: { component: "route", routeId: route.id, condition: route.condition, utilization }
      });
    }
  }
}
