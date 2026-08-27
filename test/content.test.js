import assert from "node:assert/strict";
import test from "node:test";
import { loadContent } from "../src/content/load-content.js";
import { validateContent } from "../src/content/validation.js";
import { createWorld } from "../src/simulation/world.js";

test("внешний контент загружается и остаётся неизменяемым", async () => {
  const content = await loadContent();
  assert.equal(content.resources.schemaVersion, 2);
  assert.equal(content.recipes.recipes.length, 15);
  assert.equal(content.technologies.technologies.length, 15);
  assert.equal(content.scenario.cities.length, 6);
  assert.ok(Object.isFrozen(content.technologies));
});

test("определение технологии отделено от состояния мира", async () => {
  const content = await loadContent();
  const world = createWorld(content);
  const definition = content.technologies.technologies[0];

  assert.equal("knowledge" in definition, false);
  assert.equal(Object.keys(world.cities.greenfield.technologyState).length, 15);
  assert.equal(world.cities.greenfield.technologyState.seed_selection.knowledge, 0.82);
  assert.notStrictEqual(world.cities.greenfield.technologyState.seed_selection, definition);
  assert.match(world.contentFingerprint, /^[0-9a-f]{64}$/);
});

test("ссылки технологического графа проверяются при загрузке", async () => {
  const content = structuredClone(await loadContent());
  content.technologies.relations[0].to = "missing_technology";

  assert.throws(
    () => validateContent(content),
    /ссылка на неизвестную технологию/
  );
});

test("required-связи технологического графа не могут образовать цикл", async () => {
  const content = structuredClone(await loadContent());
  content.technologies.relations.push({
    from: "water_mill",
    to: "woodworking",
    type: "required"
  });

  assert.throws(
    () => validateContent(content),
    /цикл required-связей/
  );
});
