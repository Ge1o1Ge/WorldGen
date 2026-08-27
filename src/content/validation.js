const TECHNOLOGY_RELATION_TYPES = new Set([
  "required",
  "helps",
  "enables",
  "substitutes",
  "industrial",
  "scientific",
  "supports"
]);
const SITE_POTENTIAL_TYPES = new Set(["arable", "pasture", "timber", "fish", "clay", "stone", "iron_ore"]);

function fail(path, message) {
  throw new Error(`Некорректный контент: ${path}: ${message}`);
}

function object(value, path) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    fail(path, "ожидался объект");
  }
  return value;
}

function array(value, path) {
  if (!Array.isArray(value)) {
    fail(path, "ожидался массив");
  }
  return value;
}

function string(value, path) {
  if (typeof value !== "string" || value.trim() === "") {
    fail(path, "ожидалась непустая строка");
  }
  return value;
}

function number(value, path, { min = -Infinity, max = Infinity } = {}) {
  if (!Number.isFinite(value) || value < min || value > max) {
    fail(path, `ожидалось конечное число от ${min} до ${max}`);
  }
  return value;
}

function integer(value, path, { min = -Infinity, max = Infinity } = {}) {
  number(value, path, { min, max });
  if (!Number.isInteger(value)) {
    fail(path, "ожидалось целое число");
  }
  return value;
}

function schema(document, path, supportedVersion = 1) {
  object(document, path);
  if (document.schemaVersion !== supportedVersion) {
    fail(`${path}.schemaVersion`, `поддерживается только версия ${supportedVersion}`);
  }
}

function uniqueById(items, path) {
  const ids = new Set();
  for (let index = 0; index < items.length; index += 1) {
    const id = string(items[index]?.id, `${path}[${index}].id`);
    if (ids.has(id)) {
      fail(`${path}[${index}].id`, `повторяющийся id '${id}'`);
    }
    ids.add(id);
  }
  return ids;
}

function validateAmounts(amounts, path, resourceIds) {
  object(amounts, path);
  for (const [resourceId, amount] of Object.entries(amounts)) {
    if (!resourceIds.has(resourceId)) {
      fail(`${path}.${resourceId}`, "неизвестный ресурс");
    }
    number(amount, `${path}.${resourceId}`, { min: 0 });
  }
}

function assertRequiredTechnologyGraphIsAcyclic(technologies, relations) {
  const outgoing = new Map(technologies.map(({ id }) => [id, []]));
  for (const relation of relations) {
    if (relation.type === "required") {
      outgoing.get(relation.from).push(relation.to);
    }
  }

  const visiting = new Set();
  const visited = new Set();

  function visit(id) {
    if (visiting.has(id)) {
      fail("technologies.relations", `цикл required-связей около '${id}'`);
    }
    if (visited.has(id)) return;

    visiting.add(id);
    for (const target of outgoing.get(id)) visit(target);
    visiting.delete(id);
    visited.add(id);
  }

  for (const { id } of technologies) visit(id);
}

function validateTerritoryMap(map) {
  schema(map, "map", 3);
  string(map.id, "map.id");
  string(map.name, "map.name");
  integer(map.generatorVersion, "map.generatorVersion", { min: 1 });

  object(map.grid, "map.grid");
  const width = integer(map.grid.width, "map.grid.width", { min: 10, max: 1000 });
  const height = integer(map.grid.height, "map.grid.height", { min: 10, max: 1000 });
  number(map.grid.zoneSizeMeters, "map.grid.zoneSizeMeters", { min: Number.EPSILON });
  const aggregationFactor = integer(map.grid.aggregationFactor, "map.grid.aggregationFactor", { min: 2, max: 100 });
  if (width % aggregationFactor !== 0 || height % aggregationFactor !== 0) {
    fail("map.grid.aggregationFactor", "размеры сетки должны делиться на коэффициент агрегации");
  }
  number(map.grid.vertexJitter, "map.grid.vertexJitter", { min: 0, max: 0.3 });
  integer(map.grid.seed, "map.grid.seed", { min: 0 });

  object(map.population, "map.population");
  integer(map.population.total, "map.population.total", { min: 0 });
  number(map.population.urbanConcentration, "map.population.urbanConcentration", { min: 0 });
  number(map.population.urbanRadius, "map.population.urbanRadius", { min: Number.EPSILON });

  object(map.terrain, "map.terrain");
  number(map.terrain.fertilityBase, "map.terrain.fertilityBase", { min: 0, max: 1 });
  number(map.terrain.fertilityVariation, "map.terrain.fertilityVariation", { min: 0, max: 1 });
  number(map.terrain.elevationBaseMeters, "map.terrain.elevationBaseMeters", { min: -1000, max: 10_000 });
  number(map.terrain.elevationRangeMeters, "map.terrain.elevationRangeMeters", { min: 0, max: 10_000 });
  number(map.terrain.roughness, "map.terrain.roughness", { min: 0, max: 1 });

  object(map.climate, "map.climate");
  number(map.climate.meanTemperatureC, "map.climate.meanTemperatureC", { min: -80, max: 60 });
  number(map.climate.temperatureRangeC, "map.climate.temperatureRangeC", { min: 0, max: 80 });
  number(map.climate.rainfall, "map.climate.rainfall", { min: 0, max: 1 });

  object(map.hydrology, "map.hydrology");
  number(map.hydrology.riverCenterY, "map.hydrology.riverCenterY", { min: 0, max: height - 1 });
  number(map.hydrology.riverWidthZones, "map.hydrology.riverWidthZones", { min: 0.2, max: 20 });
  number(map.hydrology.floodplainWidthZones, "map.hydrology.floodplainWidthZones", {
    min: map.hydrology.riverWidthZones,
    max: 100
  });
  number(map.hydrology.meander, "map.hydrology.meander", { min: 0, max: height / 3 });

  return { width, height };
}

function gridCoordinate(value, path, width, height) {
  object(value, path);
  integer(value.x, `${path}.x`, { min: 0, max: width - 1 });
  integer(value.y, `${path}.y`, { min: 0, max: height - 1 });
  return `${value.x}:${value.y}`;
}

export function validateContent({ resources, recipes, technologies, map, scenario }) {
  schema(resources, "resources", 2);
  const resourceItems = array(resources.resources, "resources.resources");
  const resourceIds = uniqueById(resourceItems, "resources.resources");
  for (let index = 0; index < resourceItems.length; index += 1) {
    const item = object(resourceItems[index], `resources.resources[${index}]`);
    string(item.name, `resources.resources[${index}].name`);
    string(item.unit, `resources.resources[${index}].unit`);
    string(item.category, `resources.resources[${index}].category`);
    number(item.baseValue, `resources.resources[${index}].baseValue`, { min: Number.EPSILON });
    number(item.decayPerDay, `resources.resources[${index}].decayPerDay`, { min: 0, max: 1 });
    if (item.householdNeed !== undefined) {
      object(item.householdNeed, `resources.resources[${index}].householdNeed`);
      number(item.householdNeed.perPersonPerDay,
        `resources.resources[${index}].householdNeed.perPersonPerDay`, { min: 0 });
      if (item.householdNeed.seasonality !== undefined) {
        object(item.householdNeed.seasonality,
          `resources.resources[${index}].householdNeed.seasonality`);
        number(item.householdNeed.seasonality.minimum,
          `resources.resources[${index}].householdNeed.seasonality.minimum`, { min: 0, max: 1 });
        integer(item.householdNeed.seasonality.peakDay,
          `resources.resources[${index}].householdNeed.seasonality.peakDay`, { min: 0, max: 364 });
      }
    }
  }

  schema(recipes, "recipes", 2);
  const recipeItems = array(recipes.recipes, "recipes.recipes");
  const recipeIds = uniqueById(recipeItems, "recipes.recipes");
  for (let index = 0; index < recipeItems.length; index += 1) {
    const item = object(recipeItems[index], `recipes.recipes[${index}]`);
    string(item.name, `recipes.recipes[${index}].name`);
    string(item.category, `recipes.recipes[${index}].category`);
    validateAmounts(item.inputs, `recipes.recipes[${index}].inputs`, resourceIds);
    validateAmounts(item.outputs, `recipes.recipes[${index}].outputs`, resourceIds);
    if (Object.keys(item.outputs).length === 0) {
      fail(`recipes.recipes[${index}].outputs`, "рецепт должен что-либо производить");
    }
    number(item.laborPerBatch, `recipes.recipes[${index}].laborPerBatch`, { min: Number.EPSILON });
    if (item.sitePotential !== undefined) {
      const potential = string(item.sitePotential, `recipes.recipes[${index}].sitePotential`);
      if (!SITE_POTENTIAL_TYPES.has(potential)) {
        fail(`recipes.recipes[${index}].sitePotential`, `неизвестный природный потенциал '${potential}'`);
      }
    }
    const requiredTechnologyIds = array(
      item.requiredTechnologyIds,
      `recipes.recipes[${index}].requiredTechnologyIds`
    );
    for (let technologyIndex = 0; technologyIndex < requiredTechnologyIds.length; technologyIndex += 1) {
      string(requiredTechnologyIds[technologyIndex],
        `recipes.recipes[${index}].requiredTechnologyIds[${technologyIndex}]`);
    }
    if (item.seasonality !== undefined) {
      object(item.seasonality, `recipes.recipes[${index}].seasonality`);
      number(item.seasonality.minimum, `recipes.recipes[${index}].seasonality.minimum`, { min: 0, max: 1 });
      integer(item.seasonality.peakDay, `recipes.recipes[${index}].seasonality.peakDay`, { min: 0, max: 364 });
    }
  }

  schema(technologies, "technologies", 2);
  const technologyItems = array(technologies.technologies, "technologies.technologies");
  const technologyIds = uniqueById(technologyItems, "technologies.technologies");
  for (let index = 0; index < technologyItems.length; index += 1) {
    const item = object(technologyItems[index], `technologies.technologies[${index}]`);
    string(item.name, `technologies.technologies[${index}].name`);
    string(item.domain, `technologies.technologies[${index}].domain`);
    number(item.complexity, `technologies.technologies[${index}].complexity`, { min: 0.01, max: 1 });
    number(item.diffusion, `technologies.technologies[${index}].diffusion`, { min: 0, max: 1 });
  }
  for (let recipeIndex = 0; recipeIndex < recipeItems.length; recipeIndex += 1) {
    for (const technologyId of recipeItems[recipeIndex].requiredTechnologyIds) {
      if (!technologyIds.has(technologyId)) {
        fail(`recipes.recipes[${recipeIndex}].requiredTechnologyIds`, `неизвестная технология '${technologyId}'`);
      }
    }
  }
  const technologyRelations = array(technologies.relations, "technologies.relations");
  for (let index = 0; index < technologyRelations.length; index += 1) {
    const relation = object(technologyRelations[index], `technologies.relations[${index}]`);
    const from = string(relation.from, `technologies.relations[${index}].from`);
    const to = string(relation.to, `technologies.relations[${index}].to`);
    const type = string(relation.type, `technologies.relations[${index}].type`);
    if (!technologyIds.has(from) || !technologyIds.has(to)) {
      fail(`technologies.relations[${index}]`, "ссылка на неизвестную технологию");
    }
    if (!TECHNOLOGY_RELATION_TYPES.has(type)) {
      fail(`technologies.relations[${index}].type`, `неизвестный тип '${type}'`);
    }
  }
  assertRequiredTechnologyGraphIsAcyclic(technologyItems, technologyRelations);

  const { width: mapWidth, height: mapHeight } = validateTerritoryMap(map);

  schema(scenario, "scenario", 2);
  string(scenario.id, "scenario.id");
  string(scenario.name, "scenario.name");
  string(scenario.mapFile, "scenario.mapFile");
  integer(scenario.seed, "scenario.seed", { min: 0 });
  object(scenario.calendar, "scenario.calendar");
  integer(scenario.calendar.daysPerYear, "scenario.calendar.daysPerYear", { min: 1, max: 1000 });
  integer(scenario.calendar.startYear, "scenario.calendar.startYear", { min: -100_000, max: 100_000 });
  integer(scenario.reserveDays, "scenario.reserveDays", { min: 1 });
  object(scenario.demography, "scenario.demography");
  number(scenario.demography.birthRatePerYear, "scenario.demography.birthRatePerYear", { min: 0, max: 1 });
  number(scenario.demography.deathRatePerYear, "scenario.demography.deathRatePerYear", { min: 0, max: 1 });
  number(scenario.demography.shortageMortalityMultiplier,
    "scenario.demography.shortageMortalityMultiplier", { min: 1, max: 100 });
  number(scenario.demography.monthlyMigrationShare,
    "scenario.demography.monthlyMigrationShare", { min: 0, max: 1 });
  object(scenario.lodPolicy, "scenario.lodPolicy");
  integer(scenario.lodPolicy.crisisCooldownDays, "scenario.lodPolicy.crisisCooldownDays", { min: 0 });
  integer(scenario.lodPolicy.shortageCooldownDays, "scenario.lodPolicy.shortageCooldownDays", { min: 0 });

  const cities = array(scenario.cities, "scenario.cities");
  const cityIds = uniqueById(cities, "scenario.cities");
  const anchorCoordinates = new Set();
  for (let cityIndex = 0; cityIndex < cities.length; cityIndex += 1) {
    const city = object(cities[cityIndex], `scenario.cities[${cityIndex}]`);
    string(city.name, `scenario.cities[${cityIndex}].name`);
    const anchorKey = gridCoordinate(city.anchor, `scenario.cities[${cityIndex}].anchor`, mapWidth, mapHeight);
    if (anchorCoordinates.has(anchorKey)) {
      fail(`scenario.cities[${cityIndex}].anchor`, "зона не может быть якорем двух городов");
    }
    anchorCoordinates.add(anchorKey);
    number(city.workerShare, `scenario.cities[${cityIndex}].workerShare`, { min: 0, max: 1 });
    number(city.foodPerPersonPerDay, `scenario.cities[${cityIndex}].foodPerPersonPerDay`, { min: 0 });
    validateAmounts(city.stocks, `scenario.cities[${cityIndex}].stocks`, resourceIds);
    const industries = array(city.industries, `scenario.cities[${cityIndex}].industries`);
    uniqueById(industries, `scenario.cities[${cityIndex}].industries`);
    for (let industryIndex = 0; industryIndex < industries.length; industryIndex += 1) {
      const industry = object(industries[industryIndex], `scenario.cities[${cityIndex}].industries[${industryIndex}]`);
      if (!recipeIds.has(industry.recipeId)) {
        fail(`scenario.cities[${cityIndex}].industries[${industryIndex}].recipeId`, "неизвестный рецепт");
      }
      number(industry.capacity, `scenario.cities[${cityIndex}].industries[${industryIndex}].capacity`, { min: 0 });
      gridCoordinate(industry.zone,
        `scenario.cities[${cityIndex}].industries[${industryIndex}].zone`, mapWidth, mapHeight);
    }
    const institutions = array(city.institutions, `scenario.cities[${cityIndex}].institutions`);
    uniqueById(institutions, `scenario.cities[${cityIndex}].institutions`);
    for (let institutionIndex = 0; institutionIndex < institutions.length; institutionIndex += 1) {
      const institution = object(institutions[institutionIndex],
        `scenario.cities[${cityIndex}].institutions[${institutionIndex}]`);
      string(institution.type, `scenario.cities[${cityIndex}].institutions[${institutionIndex}].type`);
      number(institution.competence,
        `scenario.cities[${cityIndex}].institutions[${institutionIndex}].competence`, { min: 0, max: 1 });
      number(institution.learningRate,
        `scenario.cities[${cityIndex}].institutions[${institutionIndex}].learningRate`, { min: 0, max: 1 });
      const priorities = array(institution.priorities,
        `scenario.cities[${cityIndex}].institutions[${institutionIndex}].priorities`);
      for (let priorityIndex = 0; priorityIndex < priorities.length; priorityIndex += 1) {
        string(priorities[priorityIndex],
          `scenario.cities[${cityIndex}].institutions[${institutionIndex}].priorities[${priorityIndex}]`);
      }
    }
    object(city.technologySeeds, `scenario.cities[${cityIndex}].technologySeeds`);
    for (const [technologyId, dimensions] of Object.entries(city.technologySeeds)) {
      if (!technologyIds.has(technologyId)) {
        fail(`scenario.cities[${cityIndex}].technologySeeds.${technologyId}`, "неизвестная технология");
      }
      array(dimensions, `scenario.cities[${cityIndex}].technologySeeds.${technologyId}`);
      if (dimensions.length !== 4) {
        fail(`scenario.cities[${cityIndex}].technologySeeds.${technologyId}`,
          "ожидались четыре значения Knowledge/Competence/Capability/Adoption");
      }
      for (let dimensionIndex = 0; dimensionIndex < dimensions.length; dimensionIndex += 1) {
        number(dimensions[dimensionIndex],
          `scenario.cities[${cityIndex}].technologySeeds.${technologyId}[${dimensionIndex}]`, { min: 0, max: 1 });
      }
    }
  }

  const importantActors = array(scenario.importantActors, "scenario.importantActors");
  uniqueById(importantActors, "scenario.importantActors");
  for (let index = 0; index < importantActors.length; index += 1) {
    const actor = object(importantActors[index], `scenario.importantActors[${index}]`);
    string(actor.name, `scenario.importantActors[${index}].name`);
    string(actor.role, `scenario.importantActors[${index}].role`);
    gridCoordinate(actor.zone, `scenario.importantActors[${index}].zone`, mapWidth, mapHeight);
    number(actor.importance, `scenario.importantActors[${index}].importance`, { min: 0, max: 1 });
    const reasons = array(actor.reasons, `scenario.importantActors[${index}].reasons`);
    for (let reasonIndex = 0; reasonIndex < reasons.length; reasonIndex += 1) {
      string(reasons[reasonIndex], `scenario.importantActors[${index}].reasons[${reasonIndex}]`);
    }
  }

  const routes = array(scenario.routes, "scenario.routes");
  uniqueById(routes, "scenario.routes");
  for (let index = 0; index < routes.length; index += 1) {
    const route = object(routes[index], `scenario.routes[${index}]`);
    if (!cityIds.has(route.a) || !cityIds.has(route.b) || route.a === route.b) {
      fail(`scenario.routes[${index}]`, "маршрут должен соединять два разных известных города");
    }
    integer(route.travelDays, `scenario.routes[${index}].travelDays`, { min: 1 });
    number(route.dailyCapacity, `scenario.routes[${index}].dailyCapacity`, { min: Number.EPSILON });
  }

  const scheduledEvents = array(scenario.scheduledEvents, "scenario.scheduledEvents");
  uniqueById(scheduledEvents, "scenario.scheduledEvents");
  for (let index = 0; index < scheduledEvents.length; index += 1) {
    const event = object(scheduledEvents[index], `scenario.scheduledEvents[${index}]`);
    if (event.type !== "workforce_multiplier") {
      fail(`scenario.scheduledEvents[${index}].type`, "неподдерживаемый тип события");
    }
    if (!cityIds.has(event.cityId)) {
      fail(`scenario.scheduledEvents[${index}].cityId`, "неизвестный город");
    }
    integer(event.startDay, `scenario.scheduledEvents[${index}].startDay`, { min: 0 });
    integer(event.durationDays, `scenario.scheduledEvents[${index}].durationDays`, { min: 1 });
    number(event.multiplier, `scenario.scheduledEvents[${index}].multiplier`, { min: 0, max: 1 });
    string(event.label, `scenario.scheduledEvents[${index}].label`);
  }

  return true;
}
