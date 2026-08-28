import { writeFile } from "node:fs/promises";
import { stateHash } from "../src/core/canonical-json.js";
import { SeededRandom } from "../src/core/seeded-random.js";
import { loadContent } from "../src/content/load-content.js";
import { createWorld, snapshotWorld } from "../src/simulation/world.js";
import { simulateDays } from "../src/simulation/simulate.js";

const content = await loadContent();
const streamNames = ["economy", "events", "institutions", "technology"];
const randomStreams = Object.fromEntries(streamNames.map((name) => {
  const random = new SeededRandom(content.scenario.seed, name);
  const values = Array.from({ length: 12 }, () => random.next());
  return [name, { values, finalState: random.state }];
}));

const territoryIds = ["zone:0:0", "zone:50:50", "zone:90:80", "zone:99:99"];
function captureTerritories(world) {
  return Object.fromEntries(territoryIds.map((id) => {
    const territory = world.spatial.territories[id];
    return [id, {
      assignedCityId: territory.assignedCityId,
      population: territory.population,
      elevationMeters: territory.elevationMeters,
      moisture: territory.moisture,
      fertility: territory.fertility,
      biome: territory.biome,
      resourcePotential: territory.resourcePotential
    }];
  }));
}

const world = createWorld(content);
const initialTerritories = captureTerritories(world);
const checkpoints = [{ day: 0, hash: stateHash(snapshotWorld(world)) }];
let day365Territories = null;
const checkpointDays = [...new Set([1, 365, 1825, 3650, ...Array.from({ length: 122 }, (_, index) => index * 30),
  ...Array.from({ length: 31 }, (_, index) => 990 + index)])]
  .filter((day) => day > 0 && day <= 3650)
  .sort((left, right) => left - right);
for (const day of checkpointDays) {
  simulateDays(world, content, day - world.day);
  checkpoints.push({ day, hash: stateHash(snapshotWorld(world)) });
  if (day === 365) day365Territories = captureTerritories(world);
}

const finalTerritories = captureTerritories(world);

const fixture = `${JSON.stringify({
  schemaVersion: 3,
  scenarioId: content.scenario.id,
  seed: content.scenario.seed,
  contentHash: stateHash(content),
  randomStreams,
  checkpoints,
  initialTerritories,
  day365Territories,
  finalTerritories
}, null, 2)}\n`;

if (process.argv.includes("--write")) {
  await writeFile(new URL("../tests/fixtures/js-parity-v1.json", import.meta.url), fixture, "utf8");
} else {
  process.stdout.write(fixture);
}
