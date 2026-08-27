import { recordEvent } from "./journal.js";
import { recalculateSpatialAggregates } from "./grid-spatial.js";

function setCityPopulation(world, cityId, targetPopulation) {
  const node = world.spatial.nodes[`city:${cityId}`];
  const territories = node.childTerritoryIds.map((id) => world.spatial.territories[id]);
  const current = territories.reduce((sum, territory) => sum + territory.population, 0);
  const weights = territories.map((territory) => Math.max(0.1, territory.population +
    (territory.id === node.anchorTerritoryId ? Math.max(10, current * 0.01) : 0)));
  const totalWeight = weights.reduce((sum, weight) => sum + weight, 0);
  let assigned = 0;
  const fractions = [];
  for (let index = 0; index < territories.length; index += 1) {
    const exact = targetPopulation * weights[index] / totalWeight;
    territories[index].population = Math.floor(exact);
    assigned += territories[index].population;
    fractions.push({ territory: territories[index], fraction: exact - Math.floor(exact) });
  }
  fractions.sort((left, right) => right.fraction - left.fraction ||
    left.territory.id.localeCompare(right.territory.id));
  for (let index = 0; index < targetPopulation - assigned; index += 1) {
    fractions[index].territory.population += 1;
  }
}

function monthlyVitalChange(world, city) {
  const policy = world.demographyPolicy;
  const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
  const birthExpected = population * policy.birthRatePerYear * 30 / world.calendar.daysPerYear +
    city.demography.birthRemainder;
  const mortalityFactor = (1.22 - city.demography.health * 0.28) *
    (city.shortage.active ? policy.shortageMortalityMultiplier : 1) *
    (Object.values(city.needs).some((need) => need.active) ? 1.18 : 1);
  const deathExpected = population * policy.deathRatePerYear * mortalityFactor * 30 /
    world.calendar.daysPerYear + city.demography.deathRemainder;
  const births = Math.floor(birthExpected);
  const deaths = Math.min(population + births, Math.floor(deathExpected));
  city.demography.birthRemainder = birthExpected - births;
  city.demography.deathRemainder = deathExpected - deaths;
  city.demography.births += births;
  city.demography.deaths += deaths;
  const infrastructureHealth = (city.infrastructure.housingCondition + city.infrastructure.sanitation) / 2;
  city.demography.health = Math.max(0.1, Math.min(1,
    city.demography.health + (infrastructureHealth - city.demography.health) * 0.035 +
      (city.shortage.active ? -0.025 : Object.values(city.needs).some((need) => need.active) ? -0.006 : 0.002)
  ));
  return { population, births, deaths, target: population + births - deaths };
}

function migrationDestination(world, sourceId) {
  const neighbors = world.routes
    .filter((route) => route.a === sourceId || route.b === sourceId)
    .map((route) => route.a === sourceId ? route.b : route.a)
    .filter((cityId) => !world.cities[cityId].shortage.active);
  return neighbors.sort((leftId, rightId) => {
    const left = world.cities[leftId];
    const right = world.cities[rightId];
    const leftPopulation = world.spatial.nodes[left.spatialNodeId].aggregate.population;
    const rightPopulation = world.spatial.nodes[right.spatialNodeId].aggregate.population;
    const leftCoverage = left.stocks.food / Math.max(0.001, leftPopulation * left.foodPerPersonPerDay);
    const rightCoverage = right.stocks.food / Math.max(0.001, rightPopulation * right.foodPerPersonPerDay);
    return rightCoverage - leftCoverage || leftId.localeCompare(rightId);
  })[0] ?? null;
}

export function advanceDemography(world) {
  if (world.day === 0 || world.day % 30 !== 0) return;
  const changes = new Map();
  for (const cityId of Object.keys(world.cities).sort()) {
    changes.set(cityId, monthlyVitalChange(world, world.cities[cityId]));
  }

  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    if (!city.shortage.active || city.shortage.episodeDays < 10) continue;
    const destinationId = migrationDestination(world, cityId);
    if (!destinationId) continue;
    const change = changes.get(cityId);
    const migrants = Math.min(change.target, Math.max(1,
      Math.floor(change.population * world.demographyPolicy.monthlyMigrationShare)
    ));
    change.target -= migrants;
    changes.get(destinationId).target += migrants;
    city.demography.emigration += migrants;
    world.cities[destinationId].demography.immigration += migrants;
    recordEvent(world, {
      type: "migration_flow",
      subjectId: cityId,
      causeIds: city.shortage.eventId ? [city.shortage.eventId] : [],
      details: { from: cityId, to: destinationId, people: migrants }
    });
  }

  for (const [cityId, change] of changes) setCityPopulation(world, cityId, change.target);
  recalculateSpatialAggregates(world);

  if (world.day % world.calendar.daysPerYear < 30) {
    for (const [cityId, change] of changes) {
      recordEvent(world, {
        type: "population_report",
        subjectId: cityId,
        details: {
          cityId,
          population: change.target,
          birthsToDate: world.cities[cityId].demography.births,
          deathsToDate: world.cities[cityId].demography.deaths
        }
      });
    }
  }
}
