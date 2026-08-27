import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { validateContent } from "./validation.js";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

async function readJson(filePath) {
  const text = await readFile(filePath, "utf8");
  try {
    return JSON.parse(text);
  } catch (error) {
    throw new Error(`Не удалось разобрать JSON '${filePath}': ${error.message}`, { cause: error });
  }
}

function deepFreeze(value) {
  if (value && typeof value === "object" && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const child of Object.values(value)) deepFreeze(child);
  }
  return value;
}

export async function loadContent({
  contentDirectory = path.join(projectRoot, "content"),
  scenarioName = "regional-smoke.json"
} = {}) {
  const scenario = await readJson(path.join(contentDirectory, "scenarios", scenarioName));
  const [resources, recipes, technologies, map] = await Promise.all([
    readJson(path.join(contentDirectory, "resources.json")),
    readJson(path.join(contentDirectory, "recipes.json")),
    readJson(path.join(contentDirectory, "technologies.json")),
    readJson(path.join(contentDirectory, "maps", scenario.mapFile))
  ]);

  const content = { resources, recipes, technologies, map, scenario };
  validateContent(content);
  return deepFreeze(content);
}
