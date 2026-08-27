import assert from "node:assert/strict";
import test from "node:test";
import { loadContent } from "../src/content/load-content.js";
import { simulateDays, stepWorld } from "../src/simulation/simulate.js";
import { createWorld, restoreWorld, snapshotWorld } from "../src/simulation/world.js";
import { stateHash } from "../src/core/canonical-json.js";

test("процедурная зона содержит физическую географию и природные потенциалы", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const zones = Object.values(world.spatial.territories);
  const biomes = new Set(zones.map((zone) => zone.biome));

  assert.equal(world.spatial.grid.generatorVersion, 1);
  assert.ok(zones.every((zone) => Number.isFinite(zone.elevationMeters)));
  assert.ok(zones.every((zone) => zone.moisture >= 0 && zone.moisture <= 1));
  assert.ok(zones.every((zone) => Object.keys(zone.resourcePotential).length === 7));
  assert.ok(zones.some((zone) => zone.water.river));
  assert.ok(zones.filter((zone) => zone.water.river).every((zone) => zone.population === 0));
  assert.ok(biomes.size >= 6);
  assert.ok(Math.max(...zones.map((zone) => zone.resourcePotential.iron_ore)) > 0.4);
});

test("в низкотехнологичном сценарии представлены все пятнадцать производств", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const instantiatedRecipes = new Set(Object.values(world.cities)
    .flatMap((city) => city.industries.map((industry) => industry.recipeId)));

  assert.equal(content.recipes.recipes.length, 15);
  assert.equal(instantiatedRecipes.size, 15);
  assert.ok(Object.values(world.cities).every((city) =>
    city.industries.every((industry) => world.spatial.territories[industry.zoneId].assignedCityId === city.id)
  ));
});

test("торговые намерения возникают локально и исполняются только после задержки", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  stepWorld(world, content);

  assert.equal(world.day, 1);
  assert.ok(world.tradeIntents.length > 0);
  assert.equal(world.shipments.length, 0);

  stepWorld(world, content);
  assert.ok(world.shipments.length > 0);
  assert.ok(world.shipments.every((shipment) => shipment.departureDay >= 1));
});

test("состояние дороги канонически меняет время пути и пропускную способность", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const initial = structuredClone(world.routes[0]);
  simulateDays(world, content, 365);
  const route = world.routes.find((candidate) => candidate.id === initial.id);

  assert.equal(route.baseTravelDays, initial.baseTravelDays);
  assert.equal(route.baseDailyCapacity, initial.baseDailyCapacity);
  assert.ok(route.condition >= 0 && route.condition <= 1);
  assert.ok(route.dailyCapacity > 0 && route.dailyCapacity <= route.baseDailyCapacity);
  assert.equal(route.travelDays, Math.max(1,
    Math.round(route.baseTravelDays * (1.32 - route.condition * 0.32))));
});

test("локальные цены реагируют на запас и входят в условия сделки", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  simulateDays(world, content, 30);
  const prices = Object.values(world.cities).map((city) => city.markets.food.price);
  const dispatch = world.journal.find((event) => event.type === "shipment_dispatched");

  assert.ok(Math.max(...prices) - Math.min(...prices) > 0.5);
  assert.ok(dispatch.details.unitPrice > 0);
  assert.ok(world.cities.harbor.markets.food.price < world.cities.northwatch.markets.food.price);
});

test("знание, компетенция, возможность и внедрение изменяются раздельно", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const initialKnowledge = world.cities.greenfield.technologyState.water_mill.knowledge;
  const initialAdoption = world.cities.greenfield.technologyState.water_mill.adoption;
  simulateDays(world, content, 365);
  const technology = world.cities.greenfield.technologyState.water_mill;

  assert.ok(technology.knowledge > initialKnowledge);
  assert.ok(technology.knowledge > technology.competence);
  assert.ok(technology.adoption >= initialAdoption);
  assert.ok(world.knowledgeTransfers.every((transfer) => transfer.arrivalDay > transfer.departureDay));
  assert.ok(world.journal.some((event) => event.type === "technology_milestone"));
});

test("сведения о кризисе приходят в другие поселения позже события", async () => {
  const content = structuredClone(await loadContent());
  content.scenario.scheduledEvents = [{
    id: "information_test_crisis", type: "workforce_multiplier", cityId: "harbor",
    startDay: 30, durationDays: 20, multiplier: 0.6, label: "Проверочная лихорадка"
  }];
  const world = createWorld(content);
  simulateDays(world, content, 31);
  const crisis = world.journal.find((event) => event.type === "crisis_started");

  assert.equal(world.cities.harbor.knowledgeState.observations[crisis.id].receivedDay, 30);
  assert.equal(world.cities.crossroads.knowledgeState.observations[crisis.id], undefined);

  simulateDays(world, content, 4);
  const observation = world.cities.crossroads.knowledgeState.observations[crisis.id];
  assert.equal(observation.receivedDay, 34);
  assert.ok(observation.confidence < 1);
  assert.ok(world.journal.some((event) =>
    event.type === "information_received" && event.causeIds.includes(crisis.id)
  ));
});

test("мир без внешнего шока сохраняет население и снабжение пять лет", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const initialPopulation = world.spatial.nodes[world.spatial.regionNodeId].aggregate.population;
  const loggingSite = world.spatial.territories[world.cities.northwatch.industries
    .find((industry) => industry.recipeId === "fell_timber").zoneId];
  const initialForest = loggingSite.naturalState.forestBiomass;
  const mineSite = world.spatial.territories[world.cities.stonebridge.industries
    .find((industry) => industry.recipeId === "mine_iron").zoneId];
  simulateDays(world, content, 365 * 5);
  const population = world.spatial.nodes[world.spatial.regionNodeId].aggregate.population;

  assert.ok(population > initialPopulation * 0.9);
  assert.ok(Object.values(world.cities).every((city) => city.shortage.days === 0));
  assert.ok(Object.values(world.cities).every((city) => city.stocks.food > 0));
  assert.ok(Object.values(world.cities).every((city) => city.infrastructure.housingCondition > 0.7));
  assert.ok(Object.values(world.cities).every((city) => city.demography.health > 0.7));
  assert.ok(loggingSite.naturalState.forestBiomass < initialForest);
  assert.ok(loggingSite.naturalState.forestBiomass > 0);
  assert.ok(mineSite.naturalState.deposits.iron_ore < 1);
  assert.equal(world.telemetry.daily.length, 730);
});

test("мир после загрузки снимка продолжает ту же детерминированную историю", async () => {
  const content = await loadContent();
  const uninterrupted = createWorld(content);
  simulateDays(uninterrupted, content, 180);
  const serialized = JSON.parse(JSON.stringify(snapshotWorld(uninterrupted)));
  const restored = restoreWorld(content, serialized);

  simulateDays(uninterrupted, content, 365);
  simulateDays(restored, content, 365);
  assert.equal(stateHash(snapshotWorld(restored)), stateHash(snapshotWorld(uninterrupted)));

  const changedContent = structuredClone(content);
  changedContent.map.climate.rainfall += 0.01;
  assert.throws(() => restoreWorld(changedContent, serialized), /Отпечаток контента/);
});

test("многолетний мир сам создаёт кризис, локальную детализацию и миграцию", async () => {
  const content = await loadContent();
  assert.equal(content.scenario.scheduledEvents.length, 0);
  const world = createWorld(content);
  simulateDays(world, content, 365 * 10);
  const crisis = world.journal.find((event) => event.type === "crisis_started");
  assert.ok(crisis);
  const macroExpansion = world.journal.find((event) =>
    event.type === "spatial_node_expanded" && event.details.kind === "macro" &&
    event.causeIds.includes(crisis.id)
  );
  const receivedReport = world.journal.find((event) =>
    event.type === "information_received" && event.causeIds.includes(crisis.id)
  );
  const migration = world.journal.find((event) => event.type === "migration_flow");

  assert.equal(crisis.details.endogenous, true);
  assert.ok(crisis.details.emergence.risk > 0);
  assert.equal(macroExpansion.details.zoneCount, 100);
  assert.ok(receivedReport.day > crisis.day);
  assert.ok(migration.day > 0);
  assert.ok(migration.details.people > 0);
});
