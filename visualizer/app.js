import { inverseIsometric, nearbyGridCells, pointInPolygon } from "/map-geometry.js";

const canvas = document.getElementById("world-map");
const context = canvas.getContext("2d");
const selection = document.getElementById("selection");
const eventList = document.getElementById("event-list");
const controls = [...document.querySelectorAll(".controls button")];
const playButton = document.getElementById("play");
const scaleSelect = document.getElementById("scale");
const layerSelect = document.getElementById("layer");
const mapWrap = canvas.parentElement;
const mapTooltip = document.getElementById("map-tooltip");
const zoomLevel = document.getElementById("zoom-level");

let bootstrap = null;
let state = null;
let selected = null;
let playTimer = null;
let requestPending = false;
let projection = null;
let pixelRatio = 1;
let camera = { zoom: 1, x: 0, y: 0 };
let pointerDrag = null;
let suppressClickUntil = 0;
let tooltipTimer = null;
let tooltipTargetKey = null;
let tooltipPointer = null;
let contourSegments = [];
let heightLabels = [];
let drawFrame = null;
const boundaryPathCache = new Map();

const MIN_ZOOM = 1;
const MAX_ZOOM = 10;
const TOOLTIP_DELAY_MS = 480;

const isDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
const palette = isDark ? {
  background: "#19201e", line: "#2b3733", route: "#9ba59f", text: "#edf2ef",
  muted: "#9ca9a4", active: "#55d6ae", shortage: "#ffb34d", crisis: "#ff6872",
  food: "#63d596", grain: "#f1be45", contour: "#b69c73", boundary: "#d8c9ad",
  forestSymbol: "#a7c39c", waterOutline: "#80b8ca", roadEdge: "#29302d", roadCore: "#c9b48d",
  cities: ["#31523b", "#4c5530", "#294b58", "#4b3d5b", "#5a4930", "#5c3a35"],
  citiesAlt: ["#3a6046", "#586238", "#315765", "#584768", "#675338", "#68413b"]
} : {
  background: "#fffdf7", line: "#e5e0d4", route: "#777b72", text: "#202725",
  muted: "#68736f", active: "#176f57", shortage: "#a45e00", crisis: "#b32632",
  food: "#2b7b50", grain: "#a36d00", contour: "#8b6846", boundary: "#5d5145",
  forestSymbol: "#315f3b", waterOutline: "#417f99", roadEdge: "#5b5146", roadCore: "#f0dcb5",
  cities: ["#cfe8c7", "#d6e3b5", "#c8dfeb", "#ded2ec", "#ead8b7", "#e7c9c0"],
  citiesAlt: ["#bdddb4", "#c8d99e", "#b5d4e3", "#d0c0e2", "#dfc89f", "#ddb7ad"]
};

const biomeColors = isDark
  ? ["#315c68", "#38584e", "#4a5c42", "#284b35", "#354832", "#596044", "#685d3d"]
  : ["#8fc7d3", "#9ebfae", "#c1c99a", "#83ad82", "#91a27d", "#cfce9a", "#d2bd83"];
const topographicBiomeColors = isDark
  ? ["#315c68", "#435d53", "#566247", "#565b45", "#565b45", "#565b45", "#665d43"]
  : ["#8fc7d3", "#a9c4b5", "#c4c999", "#d4ceaa", "#d4ceaa", "#d4ceaa", "#d2bd83"];
const biomeNames = {
  river: "река", wetland: "болото", floodplain: "пойма", forest: "лес",
  upland_forest: "нагорный лес", meadow: "луг", dry_grassland: "сухая степь"
};

function potentialColor(value, hue, alternate) {
  const lightness = isDark ? 20 + value * 34 + (alternate ? 2 : 0) : 91 - value * 42 - (alternate ? 3 : 0);
  const saturation = 22 + value * 46;
  return `hsl(${hue} ${saturation}% ${lightness}%)`;
}

function zoneFill(zone, alternate = false) {
  const layer = layerSelect.value;
  if (layer === "topographic") return topographicBiomeColors[zone[5]];
  if (layer === "political") return alternate ? palette.citiesAlt[zone[2]] : palette.cities[zone[2]];
  if (layer === "biome") return biomeColors[zone[5]];
  if (layer === "elevation") {
    const normalized = Math.max(0, Math.min(1, (zone[7] - 70) / 160));
    return potentialColor(normalized, 32 + normalized * 45, alternate);
  }
  if (layer === "moisture") return potentialColor(zone[8], 204, alternate);
  const indices = { arable: 10, timber: 12, fish: 13, clay: 14, stone: 15, iron: 16 };
  const hues = { arable: 78, timber: 132, fish: 202, clay: 28, stone: 215, iron: 8 };
  return potentialColor(zone[indices[layer]], hues[layer], alternate);
}

function macroFill(macro) {
  const layer = layerSelect.value;
  if (layer === "topographic") return topographicBiomeColors[macro[5]];
  if (layer === "political") return palette.cities[macro[2]];
  if (layer === "biome") return biomeColors[macro[5]];
  if (layer === "elevation") return potentialColor(Math.max(0, Math.min(1, (macro[3] - 70) / 160)), 55, false);
  if (layer === "moisture") return potentialColor(macro[4], 204, false);
  const indices = { arable: 6, timber: 8, fish: 9, clay: 10, stone: 11, iron: 12 };
  const hues = { arable: 78, timber: 132, fish: 202, clay: 28, stone: 215, iron: 8 };
  return potentialColor(macro[indices[layer]], hues[layer], false);
}

function hash01(seed, x, y, salt) {
  let value = (seed ^ Math.imul(x + 1, 0x9e3779b1) ^ Math.imul(y + 1, 0x85ebca77) ^ salt) >>> 0;
  value ^= value >>> 16;
  value = Math.imul(value, 0x7feb352d) >>> 0;
  value ^= value >>> 15;
  value = Math.imul(value, 0x846ca68b) >>> 0;
  value ^= value >>> 16;
  return (value >>> 0) / 4294967296;
}

function gridVertex(x, y) {
  const { width, height, vertexJitter, seed } = bootstrap.grid;
  const dx = x === 0 || x === width ? 0 : (hash01(seed, x, y, 17) - 0.5) * vertexJitter;
  const dy = y === 0 || y === height ? 0 : (hash01(seed, x, y, 71) - 0.5) * vertexJitter;
  return { x: x + dx, y: y + dy };
}

function createProjection(width, height) {
  const margin = Math.max(24, Math.min(width, height) * 0.055);
  const logicalWidth = bootstrap.grid.width * 2;
  const logicalHeight = bootstrap.grid.height;
  const scale = Math.max(0.1, Math.min((width - margin * 2) / logicalWidth, (height - margin * 2) / logicalHeight));
  const usedHeight = logicalHeight * scale;
  const top = (height - usedHeight) / 2;
  return {
    width,
    height,
    scale,
    top,
    point(value) {
      return {
        x: width / 2 + (value.x - value.y) * scale,
        y: top + (value.x + value.y) * 0.5 * scale
      };
    },
    center(x, y) {
      return this.point({ x: x + 0.5, y: y + 0.5 });
    }
  };
}

function beginPath(ctx, points) {
  ctx.beginPath();
  ctx.moveTo(points[0].x, points[0].y);
  for (let index = 1; index < points.length; index += 1) ctx.lineTo(points[index].x, points[index].y);
  ctx.closePath();
}

function cellPoints(x, y) {
  return [
    projection.point(gridVertex(x, y)),
    projection.point(gridVertex(x + 1, y)),
    projection.point(gridVertex(x + 1, y + 1)),
    projection.point(gridVertex(x, y + 1))
  ];
}

function boundaryPoints(x0, y0, x1, y1) {
  const points = [];
  for (let x = x0; x <= x1; x += 1) points.push(projection.point(gridVertex(x, y0)));
  for (let y = y0 + 1; y <= y1; y += 1) points.push(projection.point(gridVertex(x1, y)));
  for (let x = x1 - 1; x >= x0; x -= 1) points.push(projection.point(gridVertex(x, y1)));
  for (let y = y1 - 1; y > y0; y -= 1) points.push(projection.point(gridVertex(x0, y)));
  return points;
}

function zoneAt(x, y) {
  return bootstrap.zones[y * bootstrap.grid.width + x];
}

function visibleZoneBounds() {
  const corners = [
    { x: -camera.x / camera.zoom, y: -camera.y / camera.zoom },
    { x: (projection.width - camera.x) / camera.zoom, y: -camera.y / camera.zoom },
    { x: -camera.x / camera.zoom, y: (projection.height - camera.y) / camera.zoom },
    { x: (projection.width - camera.x) / camera.zoom, y: (projection.height - camera.y) / camera.zoom }
  ].map((point) => inverseIsometric(projection, point));
  return {
    x0: Math.max(0, Math.floor(Math.min(...corners.map((point) => point.x))) - 2),
    y0: Math.max(0, Math.floor(Math.min(...corners.map((point) => point.y))) - 2),
    x1: Math.min(bootstrap.grid.width - 1, Math.ceil(Math.max(...corners.map((point) => point.x))) + 2),
    y1: Math.min(bootstrap.grid.height - 1, Math.ceil(Math.max(...corners.map((point) => point.y))) + 2)
  };
}

function fillPolygon(ctx, points, color) {
  beginPath(ctx, points);
  ctx.fillStyle = color;
  ctx.fill();
  // A same-color hairline closes antialiasing seams without revealing the grid.
  ctx.strokeStyle = color;
  ctx.lineWidth = 0.65 / camera.zoom;
  ctx.stroke();
}

function drawZoneSurface(ctx) {
  const bounds = visibleZoneBounds();
  for (let y = bounds.y0; y <= bounds.y1; y += 1) {
    for (let x = bounds.x0; x <= bounds.x1; x += 1) {
      const zone = zoneAt(x, y);
      fillPolygon(ctx, cellPoints(x, y), zoneFill(zone));
    }
  }
}

function drawMacroSurface(ctx) {
  const factor = bootstrap.grid.aggregationFactor;
  for (const macro of bootstrap.macros) {
    const [x, y] = macro;
    fillPolygon(ctx, boundaryPoints(x * factor, y * factor, (x + 1) * factor, (y + 1) * factor), macroFill(macro));
  }
}

function drawRegionSurface(ctx) {
  const points = boundaryPoints(0, 0, bootstrap.grid.width, bootstrap.grid.height);
  const color = layerSelect.value === "political"
    ? (isDark ? "#30433c" : "#d5e0d7")
    : (layerSelect.value === "topographic" || layerSelect.value === "biome"
      ? (isDark ? "#4e5940" : "#c8c99d")
      : potentialColor(0.5, layerSelect.value === "moisture" ? 204 : 92, false));
  fillPolygon(ctx, points, color);
}

function interpolateContour(a, b, threshold) {
  const range = b.value - a.value;
  const ratio = Math.abs(range) < 1e-9 ? 0.5 : (threshold - a.value) / range;
  return { x: a.x + (b.x - a.x) * ratio, y: a.y + (b.y - a.y) * ratio };
}

function buildTopographicCache() {
  contourSegments = [];
  heightLabels = [];
  const elevations = bootstrap.zones.map((zone) => zone[7]);
  const minimum = Math.ceil(Math.min(...elevations) / 10) * 10;
  const maximum = Math.floor(Math.max(...elevations) / 10) * 10;
  for (let threshold = minimum; threshold <= maximum; threshold += 10) {
    for (let y = 0; y < bootstrap.grid.height - 1; y += 1) {
      for (let x = 0; x < bootstrap.grid.width - 1; x += 1) {
        const corners = [
          { x: x + 0.5, y: y + 0.5, value: zoneAt(x, y)[7] },
          { x: x + 1.5, y: y + 0.5, value: zoneAt(x + 1, y)[7] },
          { x: x + 1.5, y: y + 1.5, value: zoneAt(x + 1, y + 1)[7] },
          { x: x + 0.5, y: y + 1.5, value: zoneAt(x, y + 1)[7] }
        ];
        const crossings = [];
        for (let edge = 0; edge < 4; edge += 1) {
          const a = corners[edge];
          const b = corners[(edge + 1) % 4];
          if ((a.value < threshold) !== (b.value < threshold)) crossings.push(interpolateContour(a, b, threshold));
        }
        if (crossings.length === 2) contourSegments.push({ threshold, a: crossings[0], b: crossings[1] });
        else if (crossings.length === 4) {
          const centerHigh = corners.reduce((sum, corner) => sum + corner.value, 0) / 4 >= threshold;
          const pairs = centerHigh ? [[0, 3], [1, 2]] : [[0, 1], [2, 3]];
          for (const [a, b] of pairs) contourSegments.push({ threshold, a: crossings[a], b: crossings[b] });
        }
      }
    }
  }

  const factor = bootstrap.grid.aggregationFactor;
  for (let macroY = 0; macroY < bootstrap.grid.macroHeight; macroY += 1) {
    for (let macroX = 0; macroX < bootstrap.grid.macroWidth; macroX += 1) {
      let highest = null;
      for (let y = macroY * factor; y < (macroY + 1) * factor; y += 1) {
        for (let x = macroX * factor; x < (macroX + 1) * factor; x += 1) {
          const zone = zoneAt(x, y);
          if (!highest || zone[7] > highest.elevation) highest = { x, y, elevation: zone[7] };
        }
      }
      heightLabels.push({ ...highest, macroX, macroY });
    }
  }
}

function drawContours(ctx) {
  ctx.strokeStyle = palette.contour;
  ctx.lineCap = "round";
  for (const major of [false, true]) {
    ctx.beginPath();
    for (const segment of contourSegments) {
      if ((segment.threshold % 50 === 0) !== major) continue;
      const a = projection.point(segment.a);
      const b = projection.point(segment.b);
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
    }
    ctx.globalAlpha = major ? 0.58 : 0.28;
    ctx.lineWidth = (major ? 1.15 : 0.55) / camera.zoom;
    ctx.stroke();
  }
  ctx.globalAlpha = 1;
}

function boundaryValueAt(kind, coarse, x, y) {
  if (coarse) {
    const macro = bootstrap.macros[y * bootstrap.grid.macroWidth + x];
    if (kind === "political") return macro[2];
    const biome = bootstrap.biomes[macro[5]];
    return kind === "forest" ? ["forest", "upland_forest"].includes(biome) : ["river", "wetland"].includes(biome);
  }
  const zone = zoneAt(x, y);
  if (kind === "political") return zone[2];
  const biome = bootstrap.biomes[zone[5]];
  return kind === "forest" ? zone[9] >= 0.48 : ["river", "wetland"].includes(biome);
}

function traceBoundaryPaths(kind, coarse) {
  const cacheKey = `${kind}:${coarse ? "macro" : "zone"}`;
  if (boundaryPathCache.has(cacheKey)) return boundaryPathCache.get(cacheKey);
  const factor = coarse ? bootstrap.grid.aggregationFactor : 1;
  const width = coarse ? bootstrap.grid.macroWidth : bootstrap.grid.width;
  const height = coarse ? bootstrap.grid.macroHeight : bootstrap.grid.height;
  const segments = [];
  const vertices = new Map();
  const keyOf = (x, y) => `${x}:${y}`;
  const addSegment = (x0, y0, x1, y1) => {
    const a = keyOf(x0, y0);
    const b = keyOf(x1, y1);
    vertices.set(a, { x: x0, y: y0 });
    vertices.set(b, { x: x1, y: y1 });
    segments.push({ a, b });
  };
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const value = boundaryValueAt(kind, coarse, x, y);
      if (x + 1 < width && value !== boundaryValueAt(kind, coarse, x + 1, y)) {
        addSegment((x + 1) * factor, y * factor, (x + 1) * factor, (y + 1) * factor);
      }
      if (y + 1 < height && value !== boundaryValueAt(kind, coarse, x, y + 1)) {
        addSegment(x * factor, (y + 1) * factor, (x + 1) * factor, (y + 1) * factor);
      }
    }
  }

  const adjacency = new Map();
  segments.forEach((segment, index) => {
    for (const vertex of [segment.a, segment.b]) {
      if (!adjacency.has(vertex)) adjacency.set(vertex, []);
      adjacency.get(vertex).push(index);
    }
  });
  const used = new Set();
  const paths = [];
  const walk = (firstIndex, startVertex) => {
    const path = [vertices.get(startVertex)];
    let edgeIndex = firstIndex;
    let currentVertex = startVertex;
    while (edgeIndex !== undefined && !used.has(edgeIndex)) {
      used.add(edgeIndex);
      const edge = segments[edgeIndex];
      currentVertex = edge.a === currentVertex ? edge.b : edge.a;
      path.push(vertices.get(currentVertex));
      edgeIndex = adjacency.get(currentVertex).find((candidate) => !used.has(candidate));
      if ((adjacency.get(currentVertex).length !== 2 && currentVertex !== startVertex) || currentVertex === startVertex) break;
    }
    return path;
  };
  for (const [vertex, edgeIndices] of adjacency) {
    if (edgeIndices.length === 2) continue;
    for (const edgeIndex of edgeIndices) if (!used.has(edgeIndex)) paths.push(walk(edgeIndex, vertex));
  }
  segments.forEach((segment, index) => {
    if (!used.has(index)) paths.push(walk(index, segment.a));
  });
  boundaryPathCache.set(cacheKey, paths);
  return paths;
}

function addSmoothBoundaryPath(ctx, path) {
  const points = path.map((point) => projection.point(gridVertex(point.x, point.y)));
  if (points.length < 2) return;
  ctx.moveTo(points[0].x, points[0].y);
  if (points.length === 2) {
    ctx.lineTo(points[1].x, points[1].y);
    return;
  }
  for (let index = 1; index < points.length - 1; index += 1) {
    const midpoint = {
      x: (points[index].x + points[index + 1].x) / 2,
      y: (points[index].y + points[index + 1].y) / 2
    };
    ctx.quadraticCurveTo(points[index].x, points[index].y, midpoint.x, midpoint.y);
  }
  ctx.lineTo(points.at(-1).x, points.at(-1).y);
}

function drawBoundaryKind(ctx, kind, coarse) {
  ctx.beginPath();
  for (const path of traceBoundaryPaths(kind, coarse)) addSmoothBoundaryPath(ctx, path);
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  if (kind === "political") {
    ctx.strokeStyle = palette.boundary;
    ctx.lineWidth = 1.35 / camera.zoom;
    ctx.setLineDash([5 / camera.zoom, 3 / camera.zoom]);
    ctx.globalAlpha = 0.8;
  } else {
    ctx.strokeStyle = kind === "forest" ? palette.forestSymbol : palette.waterOutline;
    ctx.lineWidth = (kind === "forest" ? 0.85 : 1.15) / camera.zoom;
    ctx.globalAlpha = kind === "forest" ? 0.65 : 0.8;
  }
  ctx.stroke();
  ctx.setLineDash([]);
  ctx.globalAlpha = 1;
}

function drawForestSymbols(ctx) {
  const stride = camera.zoom < 2 ? 8 : camera.zoom < 4 ? 4 : 2;
  const bounds = visibleZoneBounds();
  ctx.strokeStyle = palette.forestSymbol;
  ctx.fillStyle = palette.forestSymbol;
  ctx.lineWidth = 0.8 / camera.zoom;
  ctx.globalAlpha = 0.72;
  for (let y = bounds.y0; y <= bounds.y1; y += stride) {
    for (let x = bounds.x0; x <= bounds.x1; x += stride) {
      const zone = zoneAt(x, y);
      if (zone[9] < 0.55 || hash01(bootstrap.grid.seed, x, y, 733) < 0.28) continue;
      const center = projection.center(x, y);
      const size = 3.3 / camera.zoom;
      ctx.beginPath();
      ctx.moveTo(center.x, center.y - size);
      ctx.lineTo(center.x + size * 0.78, center.y + size * 0.35);
      ctx.lineTo(center.x - size * 0.78, center.y + size * 0.35);
      ctx.closePath();
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(center.x, center.y + size * 0.25);
      ctx.lineTo(center.x, center.y + size);
      ctx.stroke();
    }
  }
  ctx.globalAlpha = 1;
}

function drawElevationLabels(ctx) {
  const stride = camera.zoom < 2 ? 3 : camera.zoom < 4 ? 2 : 1;
  const fontSize = 9 / camera.zoom;
  ctx.font = `500 ${fontSize}px Inter, system-ui, sans-serif`;
  ctx.textAlign = "left";
  ctx.textBaseline = "middle";
  for (const label of heightLabels) {
    if (label.macroX % stride !== 1 % stride || label.macroY % stride !== 1 % stride) continue;
    const center = projection.center(label.x, label.y);
    ctx.fillStyle = palette.contour;
    ctx.beginPath();
    ctx.arc(center.x, center.y, 1.25 / camera.zoom, 0, Math.PI * 2);
    ctx.fill();
    ctx.lineWidth = 2.6 / camera.zoom;
    ctx.strokeStyle = palette.background;
    ctx.strokeText(`${Math.round(label.elevation)}`, center.x + 3 / camera.zoom, center.y);
    ctx.fillText(`${Math.round(label.elevation)}`, center.x + 3 / camera.zoom, center.y);
  }
}

function drawMapSurface(ctx) {
  if (scaleSelect.value === "zone") drawZoneSurface(ctx);
  else if (scaleSelect.value === "macro") drawMacroSurface(ctx);
  else drawRegionSurface(ctx);

  const topographic = layerSelect.value === "topographic";
  if (topographic || layerSelect.value === "elevation") drawContours(ctx);
  const coarseBoundaries = scaleSelect.value !== "zone" || camera.zoom < 2;
  drawBoundaryKind(ctx, "political", coarseBoundaries);
  if (topographic) {
    drawBoundaryKind(ctx, "forest", coarseBoundaries);
    drawBoundaryKind(ctx, "water", scaleSelect.value !== "zone");
    if (scaleSelect.value === "zone") drawForestSymbols(ctx);
    drawElevationLabels(ctx);
  }
  const outline = boundaryPoints(0, 0, bootstrap.grid.width, bootstrap.grid.height);
  beginPath(ctx, outline);
  ctx.strokeStyle = palette.boundary;
  ctx.lineWidth = 1.4 / camera.zoom;
  ctx.globalAlpha = 0.75;
  ctx.stroke();
  ctx.globalAlpha = 1;
}

function rebuildStaticMap() {
  if (!bootstrap || !projection || !state) return;
  drawDynamicMap();
}

function cityStatic(cityId) {
  return bootstrap.cities.find((city) => city.id === cityId);
}

function cityDynamic(cityId) {
  return state.cities.find((city) => city.id === cityId);
}

function drawLine(ctx, from, to, color, width, dash = []) {
  ctx.beginPath();
  ctx.moveTo(from.x, from.y);
  ctx.lineTo(to.x, to.y);
  ctx.strokeStyle = color;
  ctx.lineWidth = width / camera.zoom;
  ctx.setLineDash(dash.map((value) => value / camera.zoom));
  ctx.stroke();
  ctx.setLineDash([]);
}

function drawRoutes(ctx) {
  for (let index = 0; index < bootstrap.routes.length; index += 1) {
    const route = bootstrap.routes[index];
    const fromCity = cityStatic(route.a);
    const toCity = cityStatic(route.b);
    const dynamic = state.routeStates.find((candidate) => candidate.id === route.id);
    const condition = dynamic?.condition ?? 0.68;
    const from = fromCity.anchor;
    const to = toCity.anchor;
    const dx = to.x - from.x;
    const dy = to.y - from.y;
    const length = Math.max(1, Math.hypot(dx, dy));
    const bend = (index % 2 === 0 ? 1 : -1) * Math.min(5, length * 0.12);
    const control = projection.point({
      x: (from.x + to.x) / 2 - dy / length * bend,
      y: (from.y + to.y) / 2 + dx / length * bend
    });
    const a = projection.center(from.x, from.y);
    const b = projection.center(to.x, to.y);
    ctx.beginPath();
    ctx.moveTo(a.x, a.y);
    ctx.quadraticCurveTo(control.x, control.y, b.x, b.y);
    ctx.strokeStyle = palette.roadEdge;
    ctx.lineWidth = 3.2 / camera.zoom;
    ctx.globalAlpha = 0.55 + condition * 0.4;
    ctx.stroke();
    ctx.strokeStyle = palette.roadCore;
    ctx.lineWidth = (1.1 + condition * 0.65) / camera.zoom;
    ctx.globalAlpha = 0.7 + condition * 0.3;
    ctx.stroke();
  }
  ctx.globalAlpha = 1;
}

function drawCircle(ctx, center, radius, color, width = 2) {
  ctx.beginPath();
  ctx.arc(center.x, center.y, radius / camera.zoom, 0, Math.PI * 2);
  ctx.strokeStyle = color;
  ctx.lineWidth = width / camera.zoom;
  ctx.stroke();
}

function drawDynamicMap() {
  if (!state || !projection) return;
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.clearRect(0, 0, canvas.width, canvas.height);
  context.fillStyle = palette.background;
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.setTransform(pixelRatio * camera.zoom, 0, 0, pixelRatio * camera.zoom,
    camera.x * pixelRatio, camera.y * pixelRatio);
  drawMapSurface(context);
  drawRoutes(context);

  for (const city of state.cities) {
    const fixed = cityStatic(city.id);
    const center = projection.center(fixed.anchor.x, fixed.anchor.y);
    if (city.detailed) drawCircle(context, center, 11, palette.active, 2.5);
    if (city.shortageActive) drawCircle(context, center, 15, palette.shortage, 3);
  }

  for (const macroId of state.detailedMacroIds) {
    const [, x, y] = macroId.split(":").map(Number);
    const factor = bootstrap.grid.aggregationFactor;
    const points = boundaryPoints(x * factor, y * factor, (x + 1) * factor, (y + 1) * factor);
    beginPath(context, points);
    context.strokeStyle = palette.active;
    context.lineWidth = 2.5 / camera.zoom;
    context.stroke();
  }

  for (const shipment of state.shipments) {
    const from = cityStatic(shipment.from).anchor;
    const to = cityStatic(shipment.to).anchor;
    const a = projection.center(from.x, from.y);
    const b = projection.center(to.x, to.y);
    const point = { x: a.x + (b.x - a.x) * shipment.progress, y: a.y + (b.y - a.y) * shipment.progress };
    context.beginPath();
    context.arc(point.x, point.y,
      Math.min(7, 3 + Math.sqrt(shipment.amount) / 5) / camera.zoom, 0, Math.PI * 2);
    context.fillStyle = shipment.resourceId === "food" ? palette.food : palette.grain;
    context.fill();
    context.lineWidth = 1.5 / camera.zoom;
    context.strokeStyle = palette.background;
    context.stroke();
  }

  for (const zoneId of state.crisisZoneIds) {
    const [, x, y] = zoneId.split(":").map(Number);
    const center = projection.center(x, y);
    drawCircle(context, center, 9, palette.crisis, 3);
    drawLine(context, { x: center.x - 4, y: center.y - 4 }, { x: center.x + 4, y: center.y + 4 }, palette.crisis, 2);
    drawLine(context, { x: center.x + 4, y: center.y - 4 }, { x: center.x - 4, y: center.y + 4 }, palette.crisis, 2);
  }

  for (const actor of state.actors) {
    const center = projection.center(actor.zone.x, actor.zone.y);
    const markerSize = 5 / camera.zoom;
    const y = center.y - 10 / camera.zoom;
    context.beginPath();
    context.moveTo(center.x, y - markerSize);
    context.lineTo(center.x + markerSize, y);
    context.lineTo(center.x, y + markerSize);
    context.lineTo(center.x - markerSize, y);
    context.closePath();
    context.fillStyle = palette.text;
    context.fill();
    context.strokeStyle = palette.background;
    context.lineWidth = 1.5 / camera.zoom;
    context.stroke();
  }

  const fontSize = projection.width < 620 ? 10 : 12;
  context.font = `600 ${fontSize / camera.zoom}px Inter, system-ui, sans-serif`;
  context.textAlign = "center";
  context.textBaseline = "bottom";
  for (const city of bootstrap.cities) {
    const center = projection.center(city.anchor.x, city.anchor.y);
    context.lineWidth = 4 / camera.zoom;
    context.strokeStyle = palette.background;
    context.strokeText(city.name, center.x, center.y - 13 / camera.zoom);
    context.fillStyle = palette.text;
    context.fillText(city.name, center.x, center.y - 13 / camera.zoom);
  }

  drawSelectionOutline(context);
}

function drawSelectionOutline(ctx) {
  if (!selected) return;
  let points;
  if (selected.level === "zone") points = cellPoints(selected.x, selected.y);
  else if (selected.level === "macro") {
    const factor = bootstrap.grid.aggregationFactor;
    points = boundaryPoints(selected.x * factor, selected.y * factor,
      (selected.x + 1) * factor, (selected.y + 1) * factor);
  } else points = boundaryPoints(0, 0, bootstrap.grid.width, bootstrap.grid.height);
  beginPath(ctx, points);
  ctx.strokeStyle = palette.text;
  ctx.lineWidth = 2.5 / camera.zoom;
  ctx.stroke();
}

function constrainCamera() {
  if (!projection) return;
  const minX = projection.width * (1 - camera.zoom);
  const minY = projection.height * (1 - camera.zoom);
  camera.x = Math.max(minX, Math.min(0, camera.x));
  camera.y = Math.max(minY, Math.min(0, camera.y));
}

function updateZoomControls() {
  zoomLevel.value = `${Math.round(camera.zoom * 100)}%`;
  zoomLevel.textContent = zoomLevel.value;
  document.getElementById("zoom-out").disabled = camera.zoom <= MIN_ZOOM;
  document.getElementById("zoom-in").disabled = camera.zoom >= MAX_ZOOM;
}

function scheduleMapDraw() {
  if (drawFrame !== null) return;
  drawFrame = window.requestAnimationFrame(() => {
    drawFrame = null;
    drawDynamicMap();
  });
}

function setCameraZoom(nextZoom, anchorX = projection.width / 2, anchorY = projection.height / 2) {
  const zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, nextZoom));
  if (Math.abs(zoom - camera.zoom) < 0.001) return;
  const mapX = (anchorX - camera.x) / camera.zoom;
  const mapY = (anchorY - camera.y) / camera.zoom;
  camera.x = anchorX - mapX * zoom;
  camera.y = anchorY - mapY * zoom;
  camera.zoom = zoom;
  constrainCamera();
  updateZoomControls();
  hideTooltip();
  scheduleMapDraw();
}

function resetCamera() {
  camera = { zoom: 1, x: 0, y: 0 };
  updateZoomControls();
  hideTooltip();
  drawDynamicMap();
}

function mapPointAt(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const point = {
    x: (clientX - rect.left - camera.x) / camera.zoom,
    y: (clientY - rect.top - camera.y) / camera.zoom
  };
  if (point.x < 0 || point.y < 0 || point.x >= rect.width || point.y >= rect.height) return null;
  return point;
}

function targetAt(clientX, clientY) {
  const point = mapPointAt(clientX, clientY);
  if (!point) return null;
  const logical = inverseIsometric(projection, point);
  if (scaleSelect.value === "zone") {
    const matches = nearbyGridCells(logical, bootstrap.grid.width, bootstrap.grid.height, 1)
      .filter((candidate) => pointInPolygon(point, cellPoints(candidate.x, candidate.y)))
      .sort((left, right) => {
        const leftDistance = Math.hypot(logical.x - left.x - 0.5, logical.y - left.y - 0.5);
        const rightDistance = Math.hypot(logical.x - right.x - 0.5, logical.y - right.y - 0.5);
        return leftDistance - rightDistance || left.y - right.y || left.x - right.x;
      });
    return matches[0] ? { level: "zone", ...matches[0] } : null;
  }
  if (scaleSelect.value === "macro") {
    const factor = bootstrap.grid.aggregationFactor;
    const macroLogical = { x: logical.x / factor, y: logical.y / factor };
    const matches = nearbyGridCells(macroLogical, bootstrap.grid.macroWidth, bootstrap.grid.macroHeight, 1)
      .filter((candidate) => pointInPolygon(point, boundaryPoints(
        candidate.x * factor, candidate.y * factor, (candidate.x + 1) * factor, (candidate.y + 1) * factor
      )))
      .sort((left, right) => left.y - right.y || left.x - right.x);
    return matches[0] ? { level: "macro", ...matches[0] } : null;
  }
  return pointInPolygon(point, boundaryPoints(0, 0, bootstrap.grid.width, bootstrap.grid.height))
    ? { level: "region", x: 0, y: 0 }
    : null;
}

function targetKey(target) {
  return target ? `${target.level}:${target.x}:${target.y}` : null;
}

function tooltipLine(text, secondary = false) {
  const line = document.createElement("div");
  if (secondary) line.className = "tooltip-secondary";
  line.textContent = text;
  mapTooltip.append(line);
}

function renderTooltip(target, pointer) {
  mapTooltip.replaceChildren();
  const title = document.createElement("strong");
  if (target.level === "zone") {
    const zone = zoneAt(target.x, target.y);
    const fixedCity = bootstrap.cities[zone[2]];
    const city = cityDynamic(fixedCity.id);
    const site = state.environmentalSites.find((item) => item.zone.x === target.x && item.zone.y === target.y);
    const potentials = [
      ["пашня", zone[10]], ["пастбища", zone[11]], ["лес", zone[12]], ["рыба", zone[13]],
      ["глина", zone[14]], ["камень", zone[15]], ["железо", zone[16]]
    ].sort((left, right) => right[1] - left[1]).slice(0, 3);
    title.textContent = `Зона ${target.x}:${target.y} · ${biomeNames[bootstrap.biomes[zone[5]]]}`;
    mapTooltip.append(title);
    tooltipLine(`${fixedCity.name} · ${zone[3]} чел. · 100 × 100 м`, true);
    tooltipLine(`Высота ${zone[7].toFixed(1)} м · влажность ${Math.round(zone[8] * 100)}% · плодородие ${Math.round(zone[4] * 100)}%`);
    tooltipLine(`Сильные стороны: ${potentials.map(([name, value]) => `${name} ${Math.round(value * 100)}%`).join(" · ")}`);
    tooltipLine(`Еда ${city.food.toFixed(1)} · цена ${city.markets.food.price.toFixed(2)} · здоровье ${Math.round(city.health * 100)}%`, true);
    if (site) {
      const natural = [];
      if (["grow_grain", "grow_vegetables"].includes(site.recipeId)) {
        natural.push(`почва ${Math.round(site.naturalState.soilQuality * 100)}%`);
      }
      if (["fell_timber", "gather_firewood"].includes(site.recipeId)) {
        natural.push(`лес ${Math.round(site.naturalState.forestBiomass * 100)}%`);
      }
      if (site.recipeId === "catch_fish") natural.push(`рыбный запас ${Math.round(site.naturalState.fishStock * 100)}%`);
      const depositByRecipe = { dig_clay: "clay", quarry_stone: "stone", mine_iron: "iron_ore" };
      const depositId = depositByRecipe[site.recipeId];
      if (depositId) natural.push(`остаток ${Math.round(site.naturalState.deposits[depositId] * 100)}%`);
      tooltipLine(`Площадка: ${bootstrap.recipeNames[site.recipeId] ?? site.recipeId}` +
        (natural.length > 0 ? ` · ${natural.join(" · ")}` : ""));
    }
  } else if (target.level === "macro") {
    const macro = bootstrap.macros.find((item) => item[0] === target.x && item[1] === target.y);
    title.textContent = `Нода ${target.x}:${target.y}`;
    mapTooltip.append(title);
    tooltipLine("100 зон · 1 × 1 км", true);
    tooltipLine(`Высота ${macro[3].toFixed(1)} м · влажность ${Math.round(macro[4] * 100)}%`);
    tooltipLine(`Пашня ${Math.round(macro[6] * 100)}% · лес ${Math.round(macro[8] * 100)}% · железо ${Math.round(macro[12] * 100)}%`);
  } else {
    title.textContent = bootstrap.mapName;
    mapTooltip.append(title);
    tooltipLine(`10 000 зон · 100 нод · ${state.stats.population.toLocaleString("ru-RU")} чел.`, true);
    tooltipLine(`День ${state.day} · грузов в пути ${state.stats.shipments} · состояние дорог ${Math.round(state.stats.averageRoadCondition * 100)}%`);
  }

  mapTooltip.hidden = false;
  const wrapRect = mapWrap.getBoundingClientRect();
  let left = pointer.clientX - wrapRect.left + 15;
  let top = pointer.clientY - wrapRect.top + 15;
  if (left + mapTooltip.offsetWidth > wrapRect.width - 8) {
    left = pointer.clientX - wrapRect.left - mapTooltip.offsetWidth - 15;
  }
  if (top + mapTooltip.offsetHeight > wrapRect.height - 8) {
    top = pointer.clientY - wrapRect.top - mapTooltip.offsetHeight - 15;
  }
  mapTooltip.style.left = `${Math.max(8, left)}px`;
  mapTooltip.style.top = `${Math.max(8, top)}px`;
}

function hideTooltip() {
  if (tooltipTimer !== null) window.clearTimeout(tooltipTimer);
  tooltipTimer = null;
  tooltipTargetKey = null;
  tooltipPointer = null;
  mapTooltip.hidden = true;
}

function queueTooltip(target, event) {
  const key = targetKey(target);
  tooltipPointer = { clientX: event.clientX, clientY: event.clientY };
  if (key === null) {
    hideTooltip();
    return;
  }
  if (key === tooltipTargetKey) {
    if (!mapTooltip.hidden) renderTooltip(target, tooltipPointer);
    return;
  }
  if (tooltipTimer !== null) window.clearTimeout(tooltipTimer);
  mapTooltip.hidden = true;
  tooltipTargetKey = key;
  tooltipTimer = window.setTimeout(() => {
    tooltipTimer = null;
    if (tooltipTargetKey === key && tooltipPointer) renderTooltip(target, tooltipPointer);
  }, TOOLTIP_DELAY_MS);
}

function eventText(event) {
  const rawSubject = event.subjectId ?? "";
  const cityId = event.details.cityId ?? (rawSubject.startsWith("city:") ? rawSubject.slice(5) : rawSubject);
  const cityName = state.cities.find((city) => city.id === cityId)?.name ?? rawSubject;
  const actionNames = {
    secure_food: "укрепить продовольственный резерв",
    maintain_capacity: "сохранить мощности",
    expand_specialty: "расширить специализацию",
    reduce_food_surplus: "сократить избыточное производство"
  };
  const dimensionNames = {
    knowledge: "знание", competence: "компетенция", capability: "возможность", adoption: "внедрение"
  };
  const labels = {
    crisis_started: `Начался кризис: ${event.details.label ?? cityName}`,
    crisis_ended: `Кризис завершён: ${event.details.label ?? cityName}`,
    food_shortage_started: `Начался дефицит: ${cityName}`,
    food_shortage_ended: `Дефицит завершён: ${cityName}`,
    spatial_node_expanded: event.details.kind === "macro"
      ? `Детализирована нода: ${event.details.macroNodeId}`
      : `Детализирован город: ${cityName}`,
    spatial_node_collapsed: event.details.kind === "macro"
      ? `Нода свёрнута: ${event.details.macroNodeId}`
      : `Город свёрнут: ${cityName}`,
    actor_became_significant: `Выделена значимая личность: ${rawSubject}`,
    technology_milestone: `Освоение: ${bootstrap.technologyNames[event.details.technologyId] ?? event.details.technologyId} · ` +
      `${dimensionNames[event.details.dimension] ?? event.details.dimension} ${Math.round(event.details.threshold * 100)}%`,
    migration_flow: `Миграция: ${event.details.people} чел. · ${event.details.from} → ${event.details.to}`,
    institution_decision: `Решение: ${actionNames[event.details.action] ?? event.details.action} · ${cityName}`,
    resource_shortage_started: `Дефицит «${bootstrap.resourceNames[event.details.resourceId] ?? event.details.resourceId}»: ${cityName}`,
    resource_shortage_ended: `Дефицит «${bootstrap.resourceNames[event.details.resourceId] ?? event.details.resourceId}» завершён: ${cityName}`,
    price_shock_started: `Скачок цены «${bootstrap.resourceNames[event.details.resourceId] ?? event.details.resourceId}»: ${cityName}`,
    price_shock_ended: `Цена стабилизировалась: ${bootstrap.resourceNames[event.details.resourceId] ?? event.details.resourceId} · ${cityName}`,
    infrastructure_degraded: event.details.component === "route"
      ? `Ухудшилась дорога: ${event.details.routeId}`
      : `Ухудшилась инфраструктура: ${cityName}`,
    information_received: `Получены вести о ${event.details.reportedEventType}: ${cityName}`
  };
  return labels[event.type] ?? `${event.type}: ${rawSubject || "мир"}`;
}

function renderEvents() {
  eventList.replaceChildren();
  if (state.recentEvents.length === 0) {
    const item = document.createElement("li");
    item.className = "empty";
    item.textContent = "Значимых событий пока нет";
    eventList.append(item);
    return;
  }
  for (const event of state.recentEvents) {
    const item = document.createElement("li");
    const time = document.createElement("time");
    time.textContent = `день ${event.day}`;
    item.append(time, document.createTextNode(eventText(event)));
    eventList.append(item);
  }
}

function renderLayerLegend() {
  const legend = document.getElementById("layer-legend");
  legend.replaceChildren();
  if (["topographic", "biome"].includes(layerSelect.value)) {
    bootstrap.biomes.forEach((biome, index) => {
      const item = document.createElement("span");
      const swatch = document.createElement("i");
      swatch.style.background = layerSelect.value === "topographic"
        ? topographicBiomeColors[index]
        : biomeColors[index];
      item.append(swatch, document.createTextNode(biomeNames[biome]));
      legend.append(item);
    });
    if (layerSelect.value === "topographic") {
      const notation = document.createElement("span");
      notation.textContent = "горизонтали 10 м · утолщённые 50 м · пунктир — границы поселений";
      legend.append(notation);
    }
  } else if (layerSelect.value === "political") {
    legend.textContent = "Цвет показывает область, связанную с поселением";
  } else {
    legend.textContent = "слабый потенциал ← насыщенность слоя → сильный потенциал";
  }
}

function renderSelection() {
  if (!selected) {
    selection.textContent = scaleSelect.value === "zone" ? "Выберите зону на карте" : "Выберите ноду на карте";
    return;
  }
  let title;
  let details;
  if (selected.level === "zone") {
    const zone = zoneAt(selected.x, selected.y);
    const city = state.cities[zone[2]];
    const site = state.environmentalSites.find((item) => item.zone.x === selected.x && item.zone.y === selected.y);
    const practices = city.technologies.slice(0, 3)
      .map((technology) => `${bootstrap.technologyNames[technology.id] ?? technology.id} ${Math.round(technology.adoption * 100)}%`)
      .join(", ");
    title = `Зона ${selected.x}:${selected.y}`;
    details = `${city.name} · ${zone[3]} чел. · плодородие ${Math.round(zone[4] * 100)}% · ` +
      `${biomeNames[bootstrap.biomes[zone[5]]]} · ${zone[7].toFixed(1)} м · влажность ${Math.round(zone[8] * 100)}% · ` +
      `лес ${Math.round(zone[9] * 100)}% · пашня ${Math.round(zone[10] * 100)}% · ` +
      `железо ${Math.round(zone[16] * 100)}% · здоровье ${Math.round(city.health * 100)}% · ` +
      `цена еды ${city.markets.food.price.toFixed(2)} · ограничено производств ${city.constrainedIndustries} · ` +
      `${site ? `площадка ${bootstrap.recipeNames[site.recipeId] ?? site.recipeId} · почва ${Math.round(site.naturalState.soilQuality * 100)}% · ` : ""}` +
      `практики: ${practices} · 100 × 100 м · 2 треугольника`;
  } else if (selected.level === "macro") {
    const factor = bootstrap.grid.aggregationFactor;
    let population = 0;
    const cityCounts = new Map();
    for (let y = selected.y * factor; y < (selected.y + 1) * factor; y += 1) {
      for (let x = selected.x * factor; x < (selected.x + 1) * factor; x += 1) {
        const zone = zoneAt(x, y);
        population += zone[3];
        cityCounts.set(zone[2], (cityCounts.get(zone[2]) ?? 0) + 1);
      }
    }
    const dominant = [...cityCounts.entries()].sort((a, b) => b[1] - a[1])[0][0];
    const macro = bootstrap.macros.find((item) => item[0] === selected.x && item[1] === selected.y);
    title = `Нода ${selected.x}:${selected.y}`;
    details = `100 зон · ${population.toLocaleString("ru-RU")} чел. · ${macro[3].toFixed(1)} м · ` +
      `влажность ${Math.round(macro[4] * 100)}% · преобладает ${state.cities[dominant].name}`;
  } else {
    title = bootstrap.mapName;
    const population = state.cities.reduce((sum, city) => sum + city.population, 0);
    details = `100 нод · 10 000 зон · ${population.toLocaleString("ru-RU")} чел. · 10 × 10 км`;
  }
  selection.replaceChildren();
  const strong = document.createElement("strong");
  strong.textContent = title;
  selection.append(strong, document.createTextNode(` · ${details}`));
}

function render() {
  document.getElementById("scenario-name").textContent = `${bootstrap.scenarioName} · ${bootstrap.mapName}`;
  document.getElementById("day").textContent = state.day;
  document.getElementById("operations").textContent = state.stats.operationsLastDay;
  document.getElementById("population").textContent = state.stats.population.toLocaleString("ru-RU");
  document.getElementById("active-nodes").textContent = state.stats.activeNodes;
  document.getElementById("shortage-cities").textContent = state.stats.shortageCities;
  document.getElementById("shipments").textContent = state.stats.shipments;
  document.getElementById("roads").textContent = `${Math.round(state.stats.averageRoadCondition * 100)}%`;
  document.getElementById("actors").textContent = state.stats.actors;
  document.getElementById("knowledge-transfers").textContent = state.stats.knowledgeTransfers;
  document.getElementById("reports").textContent = state.stats.reports;
  drawDynamicMap();
  renderSelection();
  renderLayerLegend();
  renderEvents();
}

function resizeCanvas() {
  const rect = canvas.getBoundingClientRect();
  if (rect.width < 1 || rect.height < 1 || !bootstrap) return;
  pixelRatio = Math.min(2, window.devicePixelRatio || 1);
  canvas.width = Math.round(rect.width * pixelRatio);
  canvas.height = Math.round(rect.height * pixelRatio);
  projection = createProjection(rect.width, rect.height);
  constrainCamera();
  rebuildStaticMap();
  if (state) drawDynamicMap();
}

async function requestState(path, method = "GET") {
  if (requestPending) return;
  requestPending = true;
  controls.forEach((button) => { button.disabled = true; });
  try {
    const response = await fetch(path, { method });
    const body = await response.json();
    if (!response.ok) throw new Error(body.error ?? `HTTP ${response.status}`);
    state = body;
    render();
  } catch (error) {
    selection.textContent = `Ошибка: ${error.message}`;
  } finally {
    requestPending = false;
    controls.forEach((button) => { button.disabled = false; });
  }
}

function stopPlaying() {
  if (playTimer !== null) window.clearInterval(playTimer);
  playTimer = null;
  playButton.textContent = "Запустить";
  playButton.setAttribute("aria-pressed", "false");
}

canvas.addEventListener("click", (event) => {
  if (performance.now() < suppressClickUntil) return;
  const target = targetAt(event.clientX, event.clientY);
  if (!target) return;
  selected = target;
  drawDynamicMap();
  renderSelection();
});

canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  const rect = canvas.getBoundingClientRect();
  const factor = Math.exp(-event.deltaY * 0.0015);
  setCameraZoom(camera.zoom * factor, event.clientX - rect.left, event.clientY - rect.top);
}, { passive: false });

canvas.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;
  pointerDrag = {
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    cameraX: camera.x,
    cameraY: camera.y,
    moved: false
  };
  canvas.setPointerCapture(event.pointerId);
  canvas.classList.add("is-dragging");
  hideTooltip();
});

canvas.addEventListener("pointermove", (event) => {
  if (!pointerDrag || pointerDrag.pointerId !== event.pointerId) {
    queueTooltip(targetAt(event.clientX, event.clientY), event);
    return;
  }
  const dx = event.clientX - pointerDrag.startX;
  const dy = event.clientY - pointerDrag.startY;
  if (Math.hypot(dx, dy) > 3) pointerDrag.moved = true;
  if (!pointerDrag.moved) return;
  camera.x = pointerDrag.cameraX + dx;
  camera.y = pointerDrag.cameraY + dy;
  constrainCamera();
  scheduleMapDraw();
});

function finishPointerDrag(event) {
  if (!pointerDrag || pointerDrag.pointerId !== event.pointerId) return;
  if (pointerDrag.moved) suppressClickUntil = performance.now() + 250;
  if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
  pointerDrag = null;
  canvas.classList.remove("is-dragging");
}

canvas.addEventListener("pointerup", finishPointerDrag);
canvas.addEventListener("pointercancel", finishPointerDrag);
canvas.addEventListener("pointerleave", () => {
  if (!pointerDrag) hideTooltip();
});

document.getElementById("zoom-in").addEventListener("click", () => setCameraZoom(camera.zoom * 1.5));
document.getElementById("zoom-out").addEventListener("click", () => setCameraZoom(camera.zoom / 1.5));
document.getElementById("reset-view").addEventListener("click", resetCamera);

scaleSelect.addEventListener("change", () => {
  selected = null;
  rebuildStaticMap();
  resetCamera();
  renderSelection();
});
layerSelect.addEventListener("change", () => {
  hideTooltip();
  rebuildStaticMap();
  drawDynamicMap();
  renderLayerLegend();
});
document.getElementById("reset").addEventListener("click", async () => {
  stopPlaying();
  selected = null;
  await requestState("/api/reset", "POST");
});
document.getElementById("step-1").addEventListener("click", () => requestState("/api/step?days=1", "POST"));
document.getElementById("step-5").addEventListener("click", () => requestState("/api/step?days=5", "POST"));
document.getElementById("step-30").addEventListener("click", () => requestState("/api/step?days=30", "POST"));
playButton.addEventListener("click", () => {
  if (playTimer !== null) {
    stopPlaying();
    return;
  }
  playButton.textContent = "Остановить";
  playButton.setAttribute("aria-pressed", "true");
  playTimer = window.setInterval(() => requestState("/api/step?days=1", "POST"), 420);
});

new ResizeObserver(() => resizeCanvas()).observe(canvas.parentElement);
const [bootstrapResponse, stateResponse] = await Promise.all([fetch("/api/bootstrap"), fetch("/api/state")]);
if (!bootstrapResponse.ok || !stateResponse.ok) throw new Error("Не удалось загрузить состояние визуализатора");
bootstrap = await bootstrapResponse.json();
state = await stateResponse.json();
buildTopographicCache();
resizeCanvas();
updateZoomControls();
render();
