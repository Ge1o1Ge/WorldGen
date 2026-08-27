import assert from "node:assert/strict";
import test from "node:test";
import { loadContent } from "../src/content/load-content.js";
import { materializeSignificantActor } from "../src/simulation/actors.js";
import { simulateDays } from "../src/simulation/simulate.js";
import { activateCityDetail, recalculateSpatialAggregates } from "../src/simulation/grid-spatial.js";
import { createWorld } from "../src/simulation/world.js";

test("города собираются из территорий, а население является производным", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const territories = Object.values(world.spatial.territories);
  const cityNodes = Object.values(world.spatial.nodes).filter((node) => node.kind === "city");

  const macroNodes = Object.values(world.spatial.nodes).filter((node) => node.kind === "macro");
  assert.equal(territories.length, 10_000);
  assert.equal(macroNodes.length, 100);
  assert.equal(cityNodes.length, 6);
  assert.ok(territories.every((territory) => territory.parentNodeId.startsWith("macro:")));
  assert.ok(territories.every((territory) => territory.triangleIds.length === 2));
  assert.ok(macroNodes.every((node) => node.childTerritoryIds.length === 100));
  assert.equal(cityNodes.reduce((sum, node) => sum + node.childTerritoryIds.length, 0), 10_000);

  const territoryPopulation = territories.reduce((total, territory) => total + territory.population, 0);
  const cityPopulation = cityNodes.reduce((total, node) => total + node.aggregate.population, 0);
  assert.equal(territoryPopulation, 17_800);
  assert.equal(cityPopulation, territoryPopulation);

  const greenfieldPopulation = world.spatial.nodes["city:greenfield"].aggregate.population;
  world.spatial.territories["zone:16:48"].population += 100;
  recalculateSpatialAggregates(world);
  assert.equal(world.spatial.nodes["city:greenfield"].aggregate.population, greenfieldPopulation + 100);
  assert.equal(world.spatial.nodes[world.spatial.regionNodeId].aggregate.population, territoryPopulation + 100);
});

test("локальный кризис временно разворачивает городскую ноду", async () => {
  const content = structuredClone(await loadContent());
  content.scenario.scheduledEvents = [{
    id: "greenfield_epidemic", type: "workforce_multiplier", cityId: "greenfield",
    startDay: 30, durationDays: 35, multiplier: 0.05, label: "Эпидемия в Зелёных Полях"
  }];
  const world = createWorld(content);
  assert.equal(world.spatial.nodes["city:greenfield"].detail, null);

  simulateDays(world, content, 31);
  const expanded = world.spatial.nodes["city:greenfield"];
  const crisis = world.journal.find((event) => event.type === "crisis_started");
  assert.ok(expanded.detail);
  assert.equal(expanded.detail.zoneCount, expanded.childTerritoryIds.length);
  assert.ok(expanded.detail.actorIds.includes("doctor_anna_lebedeva"));
  assert.ok(expanded.childTerritoryIds.includes(crisis.details.territoryId));
  const eventMacro = world.spatial.nodes[world.spatial.territories[crisis.details.territoryId].parentNodeId];
  assert.ok(eventMacro.detail);
  assert.equal(eventMacro.detail.zoneCount, 100);

  simulateDays(world, content, 89);
  assert.equal(world.spatial.nodes["city:greenfield"].detail, null);
  assert.equal(eventMacro.detail, null);
  assert.ok(world.journal.some(
    (event) => event.type === "spatial_node_collapsed" && event.subjectId === "city:greenfield"
  ));
});

test("значимая личность переживает сворачивание пространственной ноды", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const populationBefore = world.spatial.nodes["city:crossroads"].aggregate.population;
  const { actor } = materializeSignificantActor(world, {
    id: "organizer_pavel_sokolov",
    name: "Павел Соколов",
    role: "relief_organizer",
    zone: { x: 53, y: 52 },
    importance: 0.73,
    reasons: ["организовал снабжение во время кризиса"]
  });

  assert.equal(actor.representedInPopulation, true);
  assert.equal(world.spatial.nodes["city:crossroads"].aggregate.population, populationBefore);

  activateCityDetail(world, "crossroads", { keepActiveDays: 1 });
  assert.ok(world.spatial.nodes["city:crossroads"].detail.actorIds.includes(actor.id));
  simulateDays(world, content, 2);

  assert.equal(world.spatial.nodes["city:crossroads"].detail, null);
  assert.equal(world.actors[actor.id].name, "Павел Соколов");
  assert.equal(world.actors[actor.id].location.territoryId, "zone:53:52");
});
