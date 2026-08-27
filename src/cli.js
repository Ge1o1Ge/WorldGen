import { loadContent } from "./content/load-content.js";
import { stateHash } from "./core/canonical-json.js";
import { traceCauses } from "./simulation/journal.js";
import { simulateDays } from "./simulation/simulate.js";
import { createWorld, snapshotWorld } from "./simulation/world.js";

const daysArgument = process.argv[2] ?? "120";
const days = Number(daysArgument);
if (!Number.isInteger(days) || days < 0) {
  throw new Error(`Ожидалось неотрицательное количество дней, получено '${daysArgument}'`);
}

const content = await loadContent();
const world = createWorld(content);
simulateDays(world, content, days);

console.log(`Сценарий: ${content.scenario.name}`);
console.log(`Прошло дней: ${world.day}`);
console.log(`Состояние: ${stateHash(snapshotWorld(world))}`);
console.log(
  `Пространство: ${Object.keys(world.spatial.territories).length} зон → ` +
  `${Object.values(world.spatial.nodes).filter((node) => node.kind === "macro").length} нод → ` +
  `${Object.values(world.cities).length} поселений → 1 регион`
);
console.log(`Контент: ${content.resources.resources.length} ресурсов; ${content.recipes.recipes.length} производств; ` +
  `${content.technologies.technologies.length} знаний`);
console.log(`Постоянные значимые личности: ${Object.keys(world.actors).length}`);
console.log("");
console.log("Города:");
for (const city of Object.values(world.cities)) {
  const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
  const leadingTechnology = Object.entries(city.technologyState)
    .sort((left, right) => right[1].adoption - left[1].adoption || left[0].localeCompare(right[0]))[0];
  console.log(
    `- ${city.name}: population=${population}, health=${city.demography.health.toFixed(2)}, ` +
    `food=${city.stocks.food.toFixed(2)} @ ${city.markets.food.price.toFixed(2)}, ` +
    `firewood=${city.stocks.firewood.toFixed(2)}, tools=${city.stocks.tools.toFixed(2)}, ` +
    `leadingTech=${leadingTechnology[0]}:${leadingTechnology[1].adoption.toFixed(2)}, ` +
    `shortageDays=${city.shortage.days}`
  );
}

const crisisEvents = world.journal.filter((event) => event.type === "crisis_started");
const shortageEvents = world.journal.filter((event) => event.type === "food_shortage_started");
const expandedEvents = world.journal.filter((event) => event.type === "spatial_node_expanded");
const collapsedEvents = world.journal.filter((event) => event.type === "spatial_node_collapsed");
console.log("");
console.log(`События: ${world.journal.length}; кризисы: ${crisisEvents.length}; эпизоды дефицита: ${shortageEvents.length}`);
console.log(`Simulation LOD: развёрнуто нод ${expandedEvents.length}; свёрнуто нод ${collapsedEvents.length}`);
console.log(`Потоки: грузов в пути ${world.shipments.length}; торговых намерений ${world.tradeIntents.length}; ` +
  `переносов знания ${world.knowledgeTransfers.length}; курьерских сообщений ${world.information.reports.length}`);
if (crisisEvents[0]?.details.territoryId) {
  const territory = world.spatial.territories[crisisEvents[0].details.territoryId];
  console.log(`Локализация первого кризиса: ${territory.name} [${territory.id}]`);
}

const propagatedShortage = shortageEvents.find((event) => event.subjectId !== "greenfield");
if (propagatedShortage) {
  console.log("");
  console.log(`Причины события ${propagatedShortage.id} (${world.cities[propagatedShortage.subjectId].name}):`);
  for (const { depth, event } of traceCauses(world, propagatedShortage.id)) {
    console.log(`${"  ".repeat(depth)}- day ${event.day}: ${event.type} [${event.subjectId ?? "world"}]`);
  }
}
