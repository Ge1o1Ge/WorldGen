import { createRandomStreams } from "../core/seeded-random.js";
import { stateHash } from "../core/canonical-json.js";
import { createInitialActors } from "./actors.js";
import { buildSpatialHierarchy, citySpatialNodeId, zoneId } from "./grid-spatial.js";
import { createTechnologyState } from "./technology.js";

function completeStocks(stocks, resources) {
  return Object.fromEntries(
    resources.map(({ id }) => [id, Number(stocks[id] ?? 0)])
  );
}

export function createWorld(content) {
  const { scenario } = content;
  const resourceDefinitions = content.resources.resources;
  const spatial = buildSpatialHierarchy(content);
  const actors = createInitialActors(content, spatial);

  const cities = Object.fromEntries(
    [...scenario.cities]
      .sort((left, right) => left.id.localeCompare(right.id))
      .map((city) => [city.id, {
        id: city.id,
        name: city.name,
        spatialNodeId: citySpatialNodeId(city.id),
        workerShare: city.workerShare,
        foodPerPersonPerDay: city.foodPerPersonPerDay,
        localReserveDays: scenario.reserveDays,
        stocks: completeStocks(city.stocks, resourceDefinitions),
        markets: Object.fromEntries(resourceDefinitions.map((resource) => [resource.id, {
          price: resource.baseValue,
          targetStock: 0,
          coverageDays: null,
          availability: 1,
          shockActive: false,
          shockEventId: null
        }])),
        industries: [...city.industries]
          .sort((left, right) => left.id.localeCompare(right.id))
          .map((industry) => ({
            ...industry,
            zoneId: zoneId(industry.zone.x, industry.zone.y),
            initialCapacity: industry.capacity,
            lastConstraintKey: null,
            constraintEventId: null,
            totalBatches: 0
          })),
        institutions: [...city.institutions]
          .sort((left, right) => left.id.localeCompare(right.id))
          .map((institution) => ({ ...institution, priorities: [...institution.priorities], decisions: 0 })),
        activeEffects: {},
        resourceSignals: {},
        knowledgeState: { observations: {} },
        technologyState: createTechnologyState(content, city),
        demography: {
          health: 0.78,
          births: 0,
          deaths: 0,
          immigration: 0,
          emigration: 0,
          birthRemainder: 0,
          deathRemainder: 0
        },
        infrastructure: {
          housingCondition: 0.72,
          roadCondition: 0.58,
          sanitation: 0.62
        },
        needs: Object.fromEntries(resourceDefinitions
          .filter((resource) => resource.householdNeed && resource.id !== "food")
          .map((resource) => [resource.id, {
            active: false,
            days: 0,
            episodeDays: 0,
            missingStreak: 0,
            satisfiedStreak: 0,
            totalMissing: 0,
            eventId: null
          }])),
        shortage: {
          active: false,
          days: 0,
          episodeDays: 0,
          missingStreak: 0,
          satisfiedStreak: 0,
          totalFoodMissing: 0,
          eventId: null
        }
      }])
  );

  for (const city of Object.values(cities)) {
    for (const industry of city.industries) {
      const territory = spatial.territories[industry.zoneId];
      if (!territory || territory.assignedCityId !== city.id) {
        throw new Error(`Площадка предприятия '${industry.id}' не принадлежит городу '${city.id}'`);
      }
    }
  }

  return {
    schemaVersion: 2,
    scenarioId: scenario.id,
    seed: scenario.seed,
    contentFingerprint: stateHash(content),
    contentSchemaVersions: {
      resources: content.resources.schemaVersion,
      recipes: content.recipes.schemaVersion,
      technologies: content.technologies.schemaVersion,
      map: content.map.schemaVersion,
      scenario: content.scenario.schemaVersion
    },
    day: 0,
    calendar: { ...scenario.calendar },
    nextEventId: 1,
    nextShipmentId: 1,
    reserveDays: scenario.reserveDays,
    demographyPolicy: { ...scenario.demography },
    lodPolicy: { ...scenario.lodPolicy },
    spatial,
    actors,
    cities,
    routes: [...scenario.routes]
      .sort((left, right) => left.id.localeCompare(right.id))
      .map((route) => ({
        ...route,
        baseTravelDays: route.travelDays,
        baseDailyCapacity: route.dailyCapacity,
        condition: 0.68
      })),
    scheduledEvents: [...scenario.scheduledEvents]
      .sort((left, right) => left.id.localeCompare(right.id))
      .map((event) => ({ ...event })),
    shipments: [],
    tradeIntents: [],
    nextTradeIntentId: 1,
    knowledgeTransfers: [],
    nextKnowledgeTransferId: 1,
    information: { reports: [], nextReportId: 1, lastJournalIndex: 0 },
    journal: [],
    telemetry: { daily: [] },
    randomStreams: createRandomStreams(scenario.seed, ["economy", "events", "technology", "institutions"])
  };
}

export function snapshotWorld(world) {
  return {
    schemaVersion: world.schemaVersion,
    scenarioId: world.scenarioId,
    seed: world.seed,
    contentFingerprint: world.contentFingerprint,
    contentSchemaVersions: world.contentSchemaVersions,
    day: world.day,
    calendar: world.calendar,
    nextEventId: world.nextEventId,
    nextShipmentId: world.nextShipmentId,
    reserveDays: world.reserveDays,
    demographyPolicy: world.demographyPolicy,
    lodPolicy: world.lodPolicy,
    spatial: world.spatial,
    actors: world.actors,
    cities: world.cities,
    routes: world.routes,
    scheduledEvents: world.scheduledEvents,
    shipments: world.shipments,
    tradeIntents: world.tradeIntents,
    nextTradeIntentId: world.nextTradeIntentId,
    knowledgeTransfers: world.knowledgeTransfers,
    nextKnowledgeTransferId: world.nextKnowledgeTransferId,
    information: world.information,
    journal: world.journal,
    telemetry: world.telemetry,
    randomStreamStates: Object.fromEntries(
      Object.entries(world.randomStreams)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([name, stream]) => [name, stream.state])
    )
  };
}

export function restoreWorld(content, snapshot) {
  if (snapshot.schemaVersion !== 2) {
    throw new Error(`Неподдерживаемая версия снимка мира '${snapshot.schemaVersion}'`);
  }
  if (snapshot.scenarioId !== content.scenario.id) {
    throw new Error(`Снимок относится к сценарию '${snapshot.scenarioId}', а загружен '${content.scenario.id}'`);
  }
  const fingerprint = stateHash(content);
  if (snapshot.contentFingerprint !== fingerprint) {
    throw new Error("Отпечаток контента снимка не совпадает с загруженными определениями");
  }
  const expectedVersions = {
    resources: content.resources.schemaVersion,
    recipes: content.recipes.schemaVersion,
    technologies: content.technologies.schemaVersion,
    map: content.map.schemaVersion,
    scenario: content.scenario.schemaVersion
  };
  if (stateHash(snapshot.contentSchemaVersions) !== stateHash(expectedVersions)) {
    throw new Error("Версии схем контента снимка не совпадают с загруженными определениями");
  }

  const restored = structuredClone(snapshot);
  const randomStreamStates = restored.randomStreamStates;
  delete restored.randomStreamStates;
  restored.randomStreams = createRandomStreams(restored.seed, Object.keys(randomStreamStates));
  for (const [name, state] of Object.entries(randomStreamStates)) {
    restored.randomStreams[name].state = state >>> 0;
  }
  return restored;
}
