// Pure graph/view functions: shared by the editor and node:test, no browser globals.
export const LINK_TYPES = {
  required: { label: 'Необходимо', color: '#7cc9ec', description: 'Предпосылка: A необходимо для B' },
  enables: { label: 'Открывает', color: '#9bcf94', description: 'A открывает возможность B' },
  supports: { label: 'Усиливает', color: '#c4a1eb', description: 'A усиливает B' },
  helps: { label: 'Облегчает', color: '#e7c67e', description: 'A облегчает освоение B' },
  industrial: { label: 'Производство', color: '#e89977', description: 'A обеспечивает производственную базу B' },
  alternative: { label: 'Альтернатива', color: '#93aaa9', description: 'A — предложенная альтернатива B (направление сохраняется)' },
};
export const DOMAINS = { food: 'Пища и природа', construction: 'Строительство', craft: 'Ремёсла', water: 'Вода', knowledge: 'Знания', organization: 'Общество' };
export function resolveId(id, workspace) {
  return workspace.nodes.find(n => n.id === id)?.targetId ?? id;
}
export function assembleGraph(catalog, workspace) {
  const nodes = catalog.nodes.map(n => ({ ...n, status: 'source' }));
  for (const n of workspace.nodes) {
    if (n.status === 'manual') nodes.push({ ...n, layer: 'draft', technologyId: n.id, source: 'Ручной черновик', conditions: [n.conditions], effects: [n.effects] });
  }
  const ids = new Set(nodes.map(n => n.id));
  // Never hide notes if an upstream definition has been removed or renamed.
  for (const id of [...workspace.comments.map(c => resolveId(c.nodeId, workspace)), ...workspace.nodes.filter(n => n.targetId).map(n => n.targetId), ...workspace.edges.filter(e => e.status !== 'withdrawn').flatMap(e => [resolveId(e.from, workspace), resolveId(e.to, workspace)])]) {
    if (!ids.has(id)) { ids.add(id); nodes.push({ id, title: `Нет в каталоге: ${id}`, layer: 'missing', domain: 'knowledge', status: 'missing', description: 'Исходная технология отсутствует. Заметки сохранены; нужна сверка каталога.', conditions: [], effects: [] }); }
  }
  const edges = catalog.edges.map(e => ({ ...e, status: 'source' }));
  for (const e of workspace.edges.filter(e => e.status !== 'withdrawn')) {
    const edge = { ...e, from: resolveId(e.from, workspace), to: resolveId(e.to, workspace) };
    if (edge.status === 'adapted' && edges.some(c => c.from === edge.from && c.to === edge.to && c.type === edge.type)) continue;
    edges.push(edge);
  }
  for (const n of nodes) n.manualCount = (n.status === 'manual' ? 1 : 0) + workspace.comments.filter(c => c.status === 'manual' && resolveId(c.nodeId, workspace) === n.id).length;
  return { nodes, edges };
}
export function layoutGraph(nodes, edges) {
  const positions = {};
  let offsetY = 0;
  for (const layer of ['primitive', 'draft', 'catalog', 'missing']) {
    const members = nodes.filter(n => n.layer === layer);
    const ids = new Set(members.map(n => n.id));
    const rank = new Map(members.map(n => [n.id, 0]));
    // Only source prerequisites define layout. Proposed cycles remain visible, never hang the layout.
    for (let pass = 0; pass < Math.min(12, members.length); pass++) {
      let changed = false;
      for (const e of edges.filter(e => e.status === 'source' && e.type === 'required' && ids.has(e.from) && ids.has(e.to))) {
        const next = Math.min(12, rank.get(e.from) + 1);
        if (next > rank.get(e.to)) { rank.set(e.to, next); changed = true; }
      }
      if (!changed) break;
    }
    let maxY = offsetY, column = 0;
    for (const level of [...new Set(rank.values())].sort((a, b) => a - b)) {
      const species = n => /^(grow_|herd_)/.test(n.technologyId || '');
      const group = members.filter(n => rank.get(n.id) === level).sort((a, b) => Number(species(a)) - Number(species(b)) || a.domain.localeCompare(b.domain) || a.title.localeCompare(b.title, 'ru'));
      group.forEach((n, i) => {
        positions[n.id] = { x: (column + Math.floor(i / 11)) * 380, y: offsetY + (i % 11) * 112 };
        maxY = Math.max(maxY, positions[n.id].y);
      });
      column += Math.max(1, Math.ceil(group.length / 11));
    }
    if (members.length) offsetY = maxY + 280;
  }
  return positions;
}
// Coordinates belong to node IDs, not ranks/titles. Only unseen nodes need layout.
export function restoreLayout(nodes, edges, saved = {}, current = {}) {
  const positions = {};
  for (const node of nodes) {
    const position = current[node.id] ?? saved[node.id];
    if (position) positions[node.id] = { ...position };
  }
  const missing = nodes.filter(node => !positions[node.id]);
  if (missing.length) {
    const suggested = layoutGraph(nodes, edges);
    for (const node of missing) positions[node.id] = vacantPosition(suggested[node.id], positions);
  }
  return positions;
}
export function screenToWorld(point, camera) { return { x: (point.x - camera.x) / camera.scale, y: (point.y - camera.y) / camera.scale }; }
export function zoomAt(camera, point, scale) {
  scale = Math.max(.2, Math.min(2, scale));
  const world = screenToWorld(point, camera);
  return { x: point.x - world.x * scale, y: point.y - world.y * scale, scale };
}
export function edgePath(a, b, lane = 0) {
  const bend = Math.max(60, Math.abs(b.x - a.x) * .5);
  return `M ${a.x} ${a.y} C ${a.x + bend} ${a.y + lane}, ${b.x - bend} ${b.y + lane}, ${b.x} ${b.y}`;
}
export function vacantPosition(preferred, occupied) {
  const free = p => !Object.values(occupied).some(o => Math.abs(o.x - p.x) < 320 && Math.abs(o.y - p.y) < 88);
  if (free(preferred)) return preferred;
  for (let radius = 1; radius < 80; radius++) {
    for (let x = -radius; x <= radius; x++) for (const y of [-radius, radius]) {
      const candidate = { x: preferred.x + x * 340, y: preferred.y + y * 100 };
      if (free(candidate)) return candidate;
    }
    for (let y = -radius + 1; y < radius; y++) for (const x of [-radius, radius]) {
      const candidate = { x: preferred.x + x * 340, y: preferred.y + y * 100 };
      if (free(candidate)) return candidate;
    }
  }
  return { x: preferred.x, y: Math.max(0, ...Object.values(occupied).map(p => p.y)) + 150 };
}
export function prerequisiteCycles(nodes, edges) {
  const adjacency = new Map(nodes.map(n => [n.id, []]));
  for (const e of edges) if (e.type === 'required' && adjacency.has(e.from) && adjacency.has(e.to)) adjacency.get(e.from).push(e.to);
  const done = new Set(), stack = [], cycles = new Set();
  function visit(id) {
    const at = stack.indexOf(id);
    if (at >= 0) { stack.slice(at).forEach(n => cycles.add(n)); return; }
    if (done.has(id)) return;
    stack.push(id); for (const target of adjacency.get(id)) visit(target); stack.pop(); done.add(id);
  }
  for (const n of nodes) visit(n.id);
  return cycles;
}
