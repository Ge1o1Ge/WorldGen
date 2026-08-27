import assert from "node:assert/strict";
import test from "node:test";
import { loadContent } from "../src/content/load-content.js";
import { stateHash } from "../src/core/canonical-json.js";
import { traceCauses } from "../src/simulation/journal.js";
import { simulateDays } from "../src/simulation/simulate.js";
import { createWorld, snapshotWorld } from "../src/simulation/world.js";

function run(content, days = 120) {
  const world = createWorld(content);
  simulateDays(world, content, days);
  return world;
}

function withCalibrationEpidemic(content) {
  const mutable = structuredClone(content);
  mutable.scenario.scheduledEvents = [{
    id: "harbor_epidemic",
    type: "workforce_multiplier",
    cityId: "harbor",
    startDay: 30,
    durationDays: 120,
    multiplier: 0,
    label: "Тяжёлая эпидемия в Тихой Гавани"
  }];
  return mutable;
}

test("один сценарий дважды даёт идентичное состояние", async () => {
  const content = await loadContent();
  const first = run(content);
  const second = run(content);

  assert.equal(stateHash(snapshotWorld(first)), stateHash(snapshotWorld(second)));
});

test("стабильный сценарий не создаёт продовольственный дефицит", async () => {
  const content = structuredClone(await loadContent());
  content.scenario.scheduledEvents = [];
  const world = run(content);

  assert.equal(world.journal.some((event) => event.type === "food_shortage_started"), false);
});

test("эпидемия проходит по причинной цепочке до другого города", async () => {
  const content = withCalibrationEpidemic(await loadContent());
  const world = run(content, 180);
  const shortage = world.journal.find(
    (event) => event.type === "food_shortage_started" && event.subjectId !== "greenfield"
  );

  assert.ok(shortage, "ожидался дефицит за пределами города-источника кризиса");
  const causes = traceCauses(world, shortage.id).map(({ event }) => event.type);
  assert.ok(causes.includes("production_constrained"));
  assert.ok(causes.includes("crisis_started"));
});

test("материальные последствия появляются позже самого кризиса", async () => {
  const content = withCalibrationEpidemic(await loadContent());
  const world = run(content, 180);
  const crisis = world.journal.find((event) => event.type === "crisis_started");
  const shortage = world.journal.find(
    (event) => event.type === "food_shortage_started" && event.subjectId !== "greenfield"
  );

  assert.ok(shortage.day > crisis.day);
});
