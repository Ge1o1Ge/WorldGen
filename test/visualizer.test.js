import assert from "node:assert/strict";
import test from "node:test";
import { loadContent } from "../src/content/load-content.js";
import { simulateDays } from "../src/simulation/simulate.js";
import { createWorld } from "../src/simulation/world.js";
import { buildVisualizationBootstrap, buildVisualizationState } from "../src/visualizer/view-model.js";
import { inverseIsometric, nearbyGridCells, pointInPolygon } from "../visualizer/map-geometry.js";

test("геометрический hit-test карты не зависит от цвета пикселя", () => {
  const projection = { width: 800, scale: 4, top: 100 };
  const logical = { x: 37.25, y: 62.75 };
  const projected = {
    x: projection.width / 2 + (logical.x - logical.y) * projection.scale,
    y: projection.top + (logical.x + logical.y) * projection.scale * 0.5
  };

  assert.deepEqual(inverseIsometric(projection, projected), logical);
  assert.ok(pointInPolygon({ x: 5, y: 5 }, [
    { x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }, { x: 0, y: 10 }
  ]));
  assert.ok(nearbyGridCells(logical, 100, 100).some((cell) => cell.x === 37 && cell.y === 62));
});

test("визуализатор получает компактное представление реального мира", async () => {
  const content = structuredClone(await loadContent());
  content.scenario.scheduledEvents = [{
    id: "greenfield_epidemic", type: "workforce_multiplier", cityId: "greenfield",
    startDay: 30, durationDays: 35, multiplier: 0.05, label: "Эпидемия в Зелёных Полях"
  }];
  const world = createWorld(content);
  simulateDays(world, content, 31);
  const bootstrap = buildVisualizationBootstrap(world, content);
  const view = buildVisualizationState(world, content);

  assert.equal(view.day, 31);
  assert.equal(bootstrap.zones.length, 10_000);
  assert.equal(bootstrap.macros.length, 100);
  assert.equal(bootstrap.grid.levels.length, 3);
  assert.equal(bootstrap.zones[0].length, 17);
  assert.equal(bootstrap.biomes.length, 7);
  assert.equal(bootstrap.recipeNames.grow_grain, "Полевое зерноводство");
  assert.equal(view.cities.length, 6);
  assert.equal(view.stats.activeNodes, 2);
  assert.equal(view.detailedMacroIds.length, 1);
  assert.ok(view.crisisZoneIds.length > 0);
  assert.ok(view.stats.operationsLastDay > 0);
  assert.ok(view.actors.every((actor) => Number.isFinite(actor.zone.x)));
  assert.match(view.hash, /^[0-9a-f]{64}$/);
});
