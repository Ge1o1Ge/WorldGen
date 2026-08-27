export function inverseIsometric(projection, point) {
  const difference = (point.x - projection.width / 2) / projection.scale;
  const sum = (point.y - projection.top) / (projection.scale * 0.5);
  return { x: (sum + difference) / 2, y: (sum - difference) / 2 };
}

export function pointInPolygon(point, polygon) {
  let inside = false;
  for (let current = 0, previous = polygon.length - 1; current < polygon.length; previous = current, current += 1) {
    const a = polygon[current];
    const b = polygon[previous];
    const crosses = (a.y > point.y) !== (b.y > point.y) &&
      point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
    if (crosses) inside = !inside;
  }
  return inside;
}

export function nearbyGridCells(logical, width, height, radius = 1) {
  const result = [];
  const centerX = Math.floor(logical.x);
  const centerY = Math.floor(logical.y);
  for (let y = centerY - radius; y <= centerY + radius; y += 1) {
    for (let x = centerX - radius; x <= centerX + radius; x += 1) {
      if (x >= 0 && y >= 0 && x < width && y < height) result.push({ x, y });
    }
  }
  return result;
}
