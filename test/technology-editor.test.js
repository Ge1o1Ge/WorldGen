import test from 'node:test';
import assert from 'node:assert/strict';
import { assembleGraph, layoutGraph, restoreLayout, screenToWorld, zoomAt, edgePath, prerequisiteCycles, LINK_TYPES, vacantPosition } from '../visualizer/technology-graph.js';

const nodes = ['a', 'b', 'c'].map(id => ({ id, title: id, technologyId: id, layer: 'primitive', domain: 'food' }));
const catalog = { nodes, edges: [{ id: 'ab', from: 'a', to: 'b', type: 'required' }] };
const blank = () => ({ nodes: [], edges: [], comments: [], positions: {}, journal: [] });
test('camera zoom keeps the world point under the cursor, at both limits', () => {
  const camera = { x: -350, y: 125, scale: .7 }, pointer = { x: 200, y: 410 };
  for (const value of [.01, .5, 1.8, 100]) {
    const next = zoomAt(camera, pointer, value), before = screenToWorld(pointer, camera), after = screenToWorld(pointer, next);
    assert.ok(Math.abs(before.x - after.x) < 1e-9); assert.ok(Math.abs(before.y - after.y) < 1e-9);
    assert.ok(next.scale >= .2 && next.scale <= 2);
  }
});
test('adapted draft is replaced by source node but pending notes remain attached', () => {
  const state = blank(); state.nodes.push({ id: 'draft:1', title: 'Idea', status: 'adapted', targetId: 'a' });
  state.comments.push({ id: 'old', nodeId: 'draft:1', status: 'adapted', text: 'Old' }, { id: 'new', nodeId: 'draft:1', status: 'manual', text: 'New' });
  const graph = assembleGraph(catalog, state);
  assert.equal(graph.nodes.length, 3); assert.equal(graph.nodes.find(n => n.id === 'a').manualCount, 1);
  assert.equal(state.comments.length, 2);
});
test('removed source nodes retain accessible orphaned notes', () => {
  const state = blank(); state.comments.push({ nodeId: 'removed', status: 'manual' });
  assert.equal(assembleGraph(catalog, state).nodes.find(n => n.id === 'removed').layer, 'missing');
});
test('manual parallel connection types survive while adapted source duplicates are hidden', () => {
  const state = blank(); state.edges.push({ id: 'other', from: 'a', to: 'b', type: 'supports', status: 'manual' }, { id: 'duplicate', from: 'a', to: 'b', type: 'required', status: 'adapted' }, { id: 'deleted', from: 'b', to: 'a', type: 'helps', status: 'withdrawn' });
  const graph = assembleGraph(catalog, state); assert.equal(graph.edges.length, 2); assert.equal(graph.edges[1].type, 'supports');
});
test('manual prerequisite cycles are detected without treating support cycles as deadlocks', () => {
  const cycle = [...catalog.edges, { from: 'b', to: 'a', type: 'required' }];
  assert.deepEqual([...prerequisiteCycles(nodes, cycle)].sort(), ['a', 'b']);
  assert.equal(prerequisiteCycles(nodes, [...catalog.edges, { from: 'b', to: 'a', type: 'supports' }]).size, 0);
});
test('layout is deterministic, unique, and independent of manual cycles', () => {
  const graph = assembleGraph(catalog, blank());
  const first = layoutGraph(graph.nodes, graph.edges);
  assert.ok(first.a.x < first.b.x);
  assert.deepEqual(first, layoutGraph(graph.nodes, [...graph.edges, { from: 'b', to: 'a', type: 'required', status: 'manual' }]));
  assert.equal(new Set(Object.values(first).map(p => JSON.stringify(p))).size, 3);
  assert.deepEqual(layoutGraph([], []), {});
});
test('all port types have readable labels and spline endpoints remain exact', () => {
  assert.equal(Object.keys(LINK_TYPES).length, 6);
  assert.match(edgePath({ x: 10, y: 20 }, { x: -30, y: 40 }), /^M 10 20 C .*, -30 40$/);
  assert.notEqual(edgePath({ x: 10, y: 20 }, { x: 300, y: 20 }, -19), edgePath({ x: 10, y: 20 }, { x: 300, y: 20 }, 19));
});
test('a new node is placed in free space instead of covering the current node', () => {
  const preferred = { x: 380, y: 366 }, occupied = { a: preferred, b: { x: 40, y: 266 } };
  const candidate = vacantPosition(preferred, occupied);
  for (const p of Object.values(occupied)) assert.ok(Math.abs(p.x - candidate.x) >= 320 || Math.abs(p.y - candidate.y) >= 88);
  assert.deepEqual(vacantPosition(preferred, {}), preferred);
});
test('saved positions survive reload, renaming, new nodes and prerequisite changes', () => {
  const draft = { id: 'draft:1', title: 'Manual', technologyId: 'draft:1', layer: 'draft', domain: 'craft' };
  const before = [...nodes, draft];
  const saved = restoreLayout(before, catalog.edges);
  saved[draft.id] = { x: -301.75, y: 458.125 };
  saved.a = { x: 2400, y: -90 };
  const changed = [...before.map(n => ({ ...n, title: 'Новое имя ' + n.id })), { ...draft, id: 'draft:new', title: 'Новая идея' }];
  const after = restoreLayout(changed, [{ from: 'b', to: 'a', type: 'required', status: 'source' }], JSON.parse(JSON.stringify(saved)));
  for (const n of before) assert.deepEqual(after[n.id], saved[n.id]);
  assert.ok(after['draft:new']);
  const reopened = restoreLayout(changed, [], JSON.parse(JSON.stringify(after)));
  assert.deepEqual(reopened, after);
});
test('incremental layout avoids occupied manual coordinates and does not mutate saved data', () => {
  const proposed = layoutGraph(nodes, catalog.edges);
  const saved = { a: { ...proposed.b } }, original = structuredClone(saved);
  const restored = restoreLayout(nodes, catalog.edges, saved);
  assert.deepEqual(restored.a, saved.a); assert.notDeepEqual(restored.b, restored.a);
  assert.deepEqual(saved, original);
  assert.deepEqual(restoreLayout(nodes, catalog.edges, saved, { a: { x: 20, y: 30 } }).a, { x: 20, y: 30 });
});
