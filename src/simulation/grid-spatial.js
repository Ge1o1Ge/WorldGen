import { recordEvent } from "./journal.js";

export function zoneId(x, y) {
  return `zone:${x}:${y}`;
}

export function macroNodeId(x, y) {
  return `macro:${x}:${y}`;
}

export function citySpatialNodeId(cityId) {
  return `city:${cityId}`;
}

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function hash01(seed, x, y, salt = 0) {
  let value = (seed ^ Math.imul(x + 1, 0x9e3779b1) ^ Math.imul(y + 1, 0x85ebca77) ^ salt) >>> 0;
  value ^= value >>> 16;
  value = Math.imul(value, 0x7feb352d) >>> 0;
  value ^= value >>> 15;
  value = Math.imul(value, 0x846ca68b) >>> 0;
  value ^= value >>> 16;
  return (value >>> 0) / 4294967296;
}

function smoothstep(value) {
  return value * value * (3 - 2 * value);
}

function valueNoise(seed, x, y, scale, salt = 0) {
  const gx = Math.floor(x / scale);
  const gy = Math.floor(y / scale);
  const tx = smoothstep((x / scale) - gx);
  const ty = smoothstep((y / scale) - gy);
  const n00 = hash01(seed, gx, gy, salt);
  const n10 = hash01(seed, gx + 1, gy, salt);
  const n01 = hash01(seed, gx, gy + 1, salt);
  const n11 = hash01(seed, gx + 1, gy + 1, salt);
  const top = n00 + (n10 - n00) * tx;
  const bottom = n01 + (n11 - n01) * tx;
  return top + (bottom - top) * ty;
}

function fractalNoise(seed, x, y, salt = 0) {
  return valueNoise(seed, x, y, 37, salt) * 0.52 +
    valueNoise(seed, x, y, 17, salt + 101) * 0.3 +
    valueNoise(seed, x, y, 7, salt + 211) * 0.18;
}

function riverCenter(map, x) {
  const { riverCenterY, meander } = map.hydrology;
  const broad = Math.sin((x + map.grid.seed % 19) / 14) * meander * 0.5;
  const local = (valueNoise(map.grid.seed, x, 0, 21, 503) - 0.5) * meander;
  return riverCenterY + broad + local;
}

function sampleElevation(map, x, y) {
  const noise = fractalNoise(map.grid.seed, x, y, 83);
  const riverDistance = Math.abs(y - riverCenter(map, x));
  const valley = Math.exp(-riverDistance / 8) * map.terrain.elevationRangeMeters * 0.22;
  const regionalTilt = (x / map.grid.width - 0.5) * map.terrain.elevationRangeMeters * 0.12;
  return map.terrain.elevationBaseMeters +
    noise * map.terrain.elevationRangeMeters * map.terrain.roughness + regionalTilt - valley;
}

function classifyZone(map, x, y) {
  const elevationMeters = sampleElevation(map, x, y);
  const neighborElevations = [
    sampleElevation(map, x - 1, y), sampleElevation(map, x + 1, y),
    sampleElevation(map, x, y - 1), sampleElevation(map, x, y + 1)
  ];
  const maxRise = Math.max(...neighborElevations.map((value) => Math.abs(value - elevationMeters)));
  const slope = clamp(maxRise / Math.max(1, map.grid.zoneSizeMeters * 0.45), 0, 1);
  const distanceToRiver = Math.abs(y + 0.5 - riverCenter(map, x + 0.5));
  const river = distanceToRiver <= map.hydrology.riverWidthZones / 2;
  const floodplain = !river && distanceToRiver <= map.hydrology.floodplainWidthZones;
  const rainfallNoise = fractalNoise(map.grid.seed, x, y, 307);
  const riverMoisture = Math.exp(-distanceToRiver / 7) * 0.48;
  const moisture = clamp(map.climate.rainfall * 0.55 + rainfallNoise * 0.35 + riverMoisture - slope * 0.12, 0, 1);
  const temperatureC = map.climate.meanTemperatureC +
    (valueNoise(map.grid.seed, x, y, 29, 401) - 0.5) * map.climate.temperatureRangeC -
    Math.max(0, elevationMeters - map.terrain.elevationBaseMeters) * 0.0065;
  const fertilityNoise = fractalNoise(map.grid.seed, x, y, 41);
  const fertility = river ? 0 : clamp(
    map.terrain.fertilityBase + (fertilityNoise * 2 - 1) * map.terrain.fertilityVariation +
      (floodplain ? 0.18 : 0) - slope * 0.28,
    0,
    1
  );
  const rawForestCover = river ? 0 : clamp((moisture - 0.38) * 2.5 +
    (fractalNoise(map.grid.seed, x, y, 601) - 0.5) * 0.68 - (floodplain ? 0.16 : 0), 0, 1);
  const wetland = !river && distanceToRiver < map.hydrology.floodplainWidthZones * 0.65 && moisture > 0.78;
  const forestCover = wetland ? Math.min(0.45, rawForestCover) : rawForestCover;

  let biome;
  if (river) biome = "river";
  else if (wetland) biome = "wetland";
  else if (elevationMeters > map.terrain.elevationBaseMeters + map.terrain.elevationRangeMeters * 0.43 && forestCover > 0.42) biome = "upland_forest";
  else if (forestCover > 0.58) biome = "forest";
  else if (floodplain) biome = "floodplain";
  else if (moisture < 0.38) biome = "dry_grassland";
  else biome = "meadow";

  const stoneSignal = clamp(slope * 1.45 +
    (elevationMeters - map.terrain.elevationBaseMeters) / map.terrain.elevationRangeMeters * 0.45, 0, 1);
  const ironNoise = hash01(map.grid.seed, Math.floor(x / 4), Math.floor(y / 4), 911) * 0.72 +
    fractalNoise(map.grid.seed, x, y, 977) * 0.28;
  const resourcePotential = {
    arable: river || wetland ? 0 : clamp(fertility * (1 - slope) * (1 - forestCover * 0.55), 0, 1),
    pasture: river || wetland ? 0 : clamp((1 - forestCover) * (0.45 + fertility * 0.45) * (1 - slope * 0.55), 0, 1),
    timber: forestCover,
    fish: river ? 1 : clamp(Math.exp(-distanceToRiver / 2.8) * 0.38, 0, 1),
    clay: river ? 0.25 : clamp((floodplain ? 0.55 : 0.08) + moisture * 0.25 - slope * 0.5, 0, 1),
    stone: stoneSignal,
    iron_ore: clamp((ironNoise - 0.45) * 2.4, 0, 1) * (0.35 + stoneSignal * 0.65)
  };

  return {
    elevationMeters: Math.round(elevationMeters * 10) / 10,
    slope: Math.round(slope * 1000) / 1000,
    temperatureC: Math.round(temperatureC * 10) / 10,
    moisture: Math.round(moisture * 1000) / 1000,
    fertility: Math.round(fertility * 1000) / 1000,
    forestCover: Math.round(forestCover * 1000) / 1000,
    biome,
    terrain: river ? "water" : wetland ? "marsh" : slope > 0.42 ? "hills" : "plains",
    water: {
      river,
      floodplain,
      distanceToRiver: Math.round(distanceToRiver * 100) / 100
    },
    resourcePotential: Object.fromEntries(Object.entries(resourcePotential).map(([id, value]) => [
      id, Math.round(value * 1000) / 1000
    ]))
  };
}

function assignCity(x, y, cities, seed) {
  const anchor = cities.find((city) => city.anchor.x === x && city.anchor.y === y);
  if (anchor) return anchor.id;

  let best = null;
  for (let index = 0; index < cities.length; index += 1) {
    const city = cities[index];
    const distance = Math.abs(x - city.anchor.x) + Math.abs(y - city.anchor.y);
    const warp = (valueNoise(seed, x, y, 13, 1000 + index * 97) - 0.5) * 8;
    const score = distance + warp;
    if (!best || score < best.score || (score === best.score && city.id.localeCompare(best.cityId) < 0)) {
      best = { score, cityId: city.id };
    }
  }
  return best.cityId;
}

function distributePopulation(zones, totalPopulation, cityById, map) {
  const weighted = zones.map((zone) => {
    const city = cityById.get(zone.assignedCityId);
    const distance = Math.hypot(zone.grid.x - city.anchor.x, zone.grid.y - city.anchor.y);
    const urban = map.population.urbanConcentration * Math.exp(-distance / map.population.urbanRadius);
    return { zone, weight: zone.water.river ? 0 : 0.15 + zone.fertility * 0.35 + urban };
  });
  const totalWeight = weighted.reduce((sum, item) => sum + item.weight, 0);
  let assigned = 0;
  const fractions = [];

  for (const item of weighted) {
    const exact = totalPopulation * item.weight / totalWeight;
    item.zone.population = Math.floor(exact);
    assigned += item.zone.population;
    fractions.push({ zone: item.zone, fraction: exact - item.zone.population });
  }

  fractions.sort((left, right) =>
    right.fraction - left.fraction || left.zone.id.localeCompare(right.zone.id)
  );
  for (let index = 0; index < totalPopulation - assigned; index += 1) {
    fractions[index].zone.population += 1;
  }
}

function aggregateTerritories(territories) {
  const area = territories.reduce((sum, territory) => sum + territory.area, 0);
  const population = territories.reduce((sum, territory) => sum + territory.population, 0);
  const fertility = area === 0
    ? 0
    : territories.reduce((sum, territory) => sum + territory.fertility * territory.area, 0) / area;
  const resourceIds = [...new Set(territories.flatMap((territory) => Object.keys(territory.resourcePotential)))].sort();
  const resourcePotential = Object.fromEntries(resourceIds.map((resourceId) => [
    resourceId,
    territories.reduce((sum, territory) => sum + territory.resourcePotential[resourceId], 0) /
      Math.max(1, territories.length)
  ]));
  const biomeCounts = new Map();
  for (const territory of territories) {
    biomeCounts.set(territory.biome, (biomeCounts.get(territory.biome) ?? 0) + 1);
  }
  return {
    area,
    population,
    fertility,
    meanElevationMeters: territories.reduce((sum, territory) => sum + territory.elevationMeters, 0) /
      Math.max(1, territories.length),
    meanMoisture: territories.reduce((sum, territory) => sum + territory.moisture, 0) /
      Math.max(1, territories.length),
    resourcePotential,
    biomeShares: Object.fromEntries([...biomeCounts.entries()].sort().map(([biome, count]) => [
      biome, count / Math.max(1, territories.length)
    ]))
  };
}

function dominantCity(territories) {
  const counts = new Map();
  for (const territory of territories) {
    counts.set(territory.assignedCityId, (counts.get(territory.assignedCityId) ?? 0) + 1);
  }
  return [...counts.entries()].sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]))[0][0];
}

export function buildSpatialHierarchy(content) {
  const { map, scenario } = content;
  const { width, height, zoneSizeMeters, aggregationFactor, seed } = map.grid;
  const cityById = new Map(scenario.cities.map((city) => [city.id, city]));
  const zones = [];

  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const geography = classifyZone(map, x, y);
      const id = zoneId(x, y);
      zones.push({
        id,
        kind: "territory",
        name: `Зона ${x}:${y}`,
        grid: { x, y },
        area: zoneSizeMeters * zoneSizeMeters,
        population: 0,
        ...geography,
        assignedCityId: assignCity(x, y, scenario.cities, seed),
        parentNodeId: macroNodeId(Math.floor(x / aggregationFactor), Math.floor(y / aggregationFactor)),
        triangleIds: [`${id}:a`, `${id}:b`],
        diagonal: ((x + y + seed) & 1) === 0 ? "nw-se" : "ne-sw",
        naturalState: {
          soilQuality: geography.fertility,
          forestBiomass: geography.forestCover,
          fishStock: geography.resourcePotential.fish,
          deposits: {
            clay: geography.resourcePotential.clay > 0 ? 1 : 0,
            stone: geography.resourcePotential.stone > 0 ? 1 : 0,
            iron_ore: geography.resourcePotential.iron_ore > 0 ? 1 : 0
          },
          extractedBatches: {}
        }
      });
    }
  }

  distributePopulation(zones, map.population.total, cityById, map);
  const territories = Object.fromEntries(zones.map((zone) => [zone.id, zone]));
  const regionNodeId = `region:${scenario.id}`;
  const nodes = {};
  const macroWidth = width / aggregationFactor;
  const macroHeight = height / aggregationFactor;

  for (let my = 0; my < macroHeight; my += 1) {
    for (let mx = 0; mx < macroWidth; mx += 1) {
      const childTerritoryIds = [];
      for (let y = my * aggregationFactor; y < (my + 1) * aggregationFactor; y += 1) {
        for (let x = mx * aggregationFactor; x < (mx + 1) * aggregationFactor; x += 1) {
          childTerritoryIds.push(zoneId(x, y));
        }
      }
      const children = childTerritoryIds.map((id) => territories[id]);
      nodes[macroNodeId(mx, my)] = {
        id: macroNodeId(mx, my),
        kind: "macro",
        grid: { x: mx, y: my },
        parentNodeId: regionNodeId,
        childTerritoryIds,
        dominantCityId: dominantCity(children),
        aggregate: aggregateTerritories(children),
        detail: null,
        activeUntilDay: null
      };
    }
  }

  const macroNodeIds = Object.values(nodes).filter((node) => node.kind === "macro").map((node) => node.id).sort();
  nodes[regionNodeId] = {
    id: regionNodeId,
    kind: "region",
    worldEntityId: scenario.id,
    name: map.name,
    parentNodeId: null,
    childNodeIds: macroNodeIds,
    overlayNodeIds: scenario.cities.map((city) => citySpatialNodeId(city.id)).sort(),
    aggregate: aggregateTerritories(zones)
  };

  for (const city of [...scenario.cities].sort((left, right) => left.id.localeCompare(right.id))) {
    const childTerritoryIds = zones.filter((zone) => zone.assignedCityId === city.id).map((zone) => zone.id);
    nodes[citySpatialNodeId(city.id)] = {
      id: citySpatialNodeId(city.id),
      kind: "city",
      projection: "settlement",
      worldEntityId: city.id,
      name: city.name,
      parentNodeId: regionNodeId,
      anchorTerritoryId: zoneId(city.anchor.x, city.anchor.y),
      childTerritoryIds,
      aggregate: aggregateTerritories(childTerritoryIds.map((id) => territories[id])),
      detail: null,
      activeUntilDay: null
    };
  }

  return {
    regionNodeId,
    grid: {
      width,
      height,
      zoneSizeMeters,
      aggregationFactor,
      macroWidth,
      macroHeight,
      vertexJitter: map.grid.vertexJitter,
      seed,
      generatorVersion: map.generatorVersion,
      levels: [
        { level: 0, kind: "zone", width, height, scale: 1 },
        { level: 1, kind: "macro", width: macroWidth, height: macroHeight, scale: aggregationFactor },
        { level: 2, kind: "region", width: 1, height: 1, scale: width }
      ]
    },
    territories,
    nodes
  };
}

function createDetailOverlay(world, node, causeEventId) {
  const territoryIds = new Set(node.childTerritoryIds);
  const actorIds = Object.values(world.actors)
    .filter((actor) => territoryIds.has(actor.location.territoryId))
    .map(({ id }) => id)
    .sort();
  return {
    expandedDay: world.day,
    triggerEventIds: causeEventId ? [causeEventId] : [],
    zoneCount: node.childTerritoryIds.length,
    actorIds,
    expansionEventId: null
  };
}

export function recalculateSpatialAggregates(world) {
  const macroNodes = Object.values(world.spatial.nodes).filter((node) => node.kind === "macro");
  for (const node of macroNodes) {
    const territories = node.childTerritoryIds.map((id) => world.spatial.territories[id]);
    node.aggregate = aggregateTerritories(territories);
    node.dominantCityId = dominantCity(territories);
  }

  const cityNodes = Object.values(world.spatial.nodes).filter((node) => node.kind === "city");
  for (const node of cityNodes) {
    node.aggregate = aggregateTerritories(node.childTerritoryIds.map((id) => world.spatial.territories[id]));
  }

  const region = world.spatial.nodes[world.spatial.regionNodeId];
  region.aggregate = aggregateTerritories(Object.values(world.spatial.territories));
  return world.spatial;
}

export function activateCityDetail(world, cityId, { causeEventId = null, keepActiveDays = 1 } = {}) {
  const node = world.spatial.nodes[citySpatialNodeId(cityId)];
  if (!node || node.kind !== "city") throw new Error(`Неизвестная городская нода '${cityId}'`);
  if (!Number.isInteger(keepActiveDays) || keepActiveDays < 1) {
    throw new Error("Период детализации должен быть положительным целым числом");
  }

  if (node.detail === null) {
    node.detail = createDetailOverlay(world, node, causeEventId);
    const event = recordEvent(world, {
      type: "spatial_node_expanded",
      subjectId: node.id,
      causeIds: causeEventId ? [causeEventId] : [],
      details: { cityId, zoneCount: node.detail.zoneCount, actorIds: node.detail.actorIds }
    });
    node.detail.expansionEventId = event.id;
  } else if (causeEventId && !node.detail.triggerEventIds.includes(causeEventId)) {
    node.detail.triggerEventIds.push(causeEventId);
    node.detail.triggerEventIds.sort();
  }

  node.activeUntilDay = Math.max(node.activeUntilDay ?? 0, world.day + keepActiveDays);
  return node;
}

export function activateTerritoryDetail(world, territoryId, { causeEventId = null, keepActiveDays = 1 } = {}) {
  const territory = world.spatial.territories[territoryId];
  if (!territory) throw new Error(`Неизвестная территория '${territoryId}'`);
  const node = world.spatial.nodes[territory.parentNodeId];
  if (!node || node.kind !== "macro") throw new Error(`У территории '${territoryId}' нет макроноды`);
  if (!Number.isInteger(keepActiveDays) || keepActiveDays < 1) {
    throw new Error("Период детализации должен быть положительным целым числом");
  }
  if (node.detail === null) {
    node.detail = createDetailOverlay(world, node, causeEventId);
    const event = recordEvent(world, {
      type: "spatial_node_expanded",
      subjectId: node.id,
      causeIds: causeEventId ? [causeEventId] : [],
      details: { kind: "macro", macroNodeId: node.id, territoryId, zoneCount: node.detail.zoneCount, actorIds: node.detail.actorIds }
    });
    node.detail.expansionEventId = event.id;
  } else if (causeEventId && !node.detail.triggerEventIds.includes(causeEventId)) {
    node.detail.triggerEventIds.push(causeEventId);
    node.detail.triggerEventIds.sort();
  }
  node.activeUntilDay = Math.max(node.activeUntilDay ?? 0, world.day + keepActiveDays);
  return node;
}

export function collapseExpiredSpatialNodes(world) {
  for (const node of Object.values(world.spatial.nodes).sort((left, right) => left.id.localeCompare(right.id))) {
    if (!["city", "macro"].includes(node.kind) || node.detail === null || node.activeUntilDay > world.day) continue;
    recordEvent(world, {
      type: "spatial_node_collapsed",
      subjectId: node.id,
      causeIds: node.detail.expansionEventId ? [node.detail.expansionEventId] : [],
      details: {
        kind: node.kind,
        cityId: node.kind === "city" ? node.worldEntityId : null,
        macroNodeId: node.kind === "macro" ? node.id : null,
        activeDays: world.day - node.detail.expandedDay
      }
    });
    node.detail = null;
    node.activeUntilDay = null;
  }
}

export function locateEventTerritory(world, cityId, randomStream) {
  const node = world.spatial.nodes[citySpatialNodeId(cityId)];
  const territories = node.childTerritoryIds.map((id) => world.spatial.territories[id]);
  const totalWeight = territories.reduce((sum, territory) => sum + territory.population, 0);
  if (totalWeight === 0) return node.anchorTerritoryId;
  let position = randomStream.next() * totalWeight;
  for (const territory of territories) {
    position -= territory.population;
    if (position <= 0) return territory.id;
  }
  return territories.at(-1).id;
}
