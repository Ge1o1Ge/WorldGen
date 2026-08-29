import { symbolSvg } from './map-symbols.js';
import { preventGraphSelection, preventGraphNativeDrag } from './technology-interaction.js';
import { LINK_TYPES, DOMAINS, assembleGraph, resolveId, restoreLayout, screenToWorld, zoomAt, edgePath, prerequisiteCycles, vacantPosition, snapNodePosition } from './technology-graph.js';

const $ = id => document.getElementById(id);
const canvas = $('canvas'), world = $('world'), wires = $('wires'), nodeLayer = $('nodes');
const recoveryKey = 'worldgen-technology-editor-v1';
let data, atlas, graph, visible = [], positions = {}, selected = null, selectedEdge = null;
let camera = { x: 60, y: 160, scale: .85 }, pendingOnly = false, expanded = new Set();
let connection = null, gesture = null, editingId = null, sending = 0, queue = Promise.resolve();
let noteDrafts = {}, formDraft = null, saveError = false, noticeTimer, renderFrame;
const cards = new Map();
try { const recovery = JSON.parse(localStorage.getItem(recoveryKey) || '{}'); noteDrafts = recovery.notes || {}; formDraft = recovery.form || null; } catch { /* Storage is optional; server is authoritative. */ }

function el(tag, cls, text) { const element = document.createElement(tag); if (cls) element.className = cls; if (text != null) element.textContent = text; return element; }
function button(text, action, cls = '') { const b = el('button', cls, text); b.type = 'button'; b.addEventListener('click', action); return b; }
function persistText() { try { localStorage.setItem(recoveryKey, JSON.stringify({ notes: noteDrafts, form: formDraft })); } catch { /* Export remains available. */ } }
function notice(message, error = false, persistent = false) {
  clearTimeout(noticeTimer); $('notice').textContent = message; $('notice').classList.toggle('error', error); $('notice').hidden = false;
  if (!persistent) noticeTimer = setTimeout(() => { $('notice').hidden = true; }, 7000);
}
function saveState() {
  $('save-state').textContent = sending ? 'Сохранение…' : saveError ? 'Не сохранено · проверь сообщение' : data ? `Сохранено · ревизия ${data.workspace.revision}` : 'Подключение…';
  $('save-state').classList.toggle('error', saveError);
}
async function command(payload, { quietConflict = false } = {}) {
  sending++; saveState();
  const task = queue.catch(() => {}).then(async () => {
    const response = await fetch('/api/technology-editor/commands', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-WorldGen-Editor': '1' },
      body: JSON.stringify({ ...payload, revision: data.workspace.revision, catalogVersion: data.catalogVersion }),
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) { const error = new Error(result.error || result.detail || `Сохранение не выполнено (${response.status}).`); error.status = response.status; throw error; }
    data.workspace = result; saveError = false; return result;
  });
  queue = task;
  try { return await task; }
  catch (error) { if (!quietConflict || error.status !== 409) { saveError = true; notice(`${error.message}\nТекст остаётся в форме. Можно экспортировать локальные наброски перед обновлением.`, true, true); } throw error; }
  finally { sending--; saveState(); }
}
async function pinInitialLayout() {
  for (let attempt = 0; attempt < 3; attempt++) {
    // Missing-source placeholders are view-only and cannot be moved by the backend.
    const missing = Object.fromEntries(graph.nodes.filter(n => n.layer !== 'missing' && !data.workspace.positions[n.id]).map(n => [n.id, positions[n.id]]));
    if (!Object.keys(missing).length) return;
    try { await command({ action: 'move-nodes', positions: missing }, { quietConflict: true }); return; }
    catch (error) {
      if (error.status !== 409 || attempt === 2) throw error;
      // A definite 409 did not commit. Refresh and fill ONLY still-missing IDs;
      // never replay old coordinates over a move made in another tab.
      const response = await fetch('/api/technology-editor', { cache: 'no-store' });
      if (!response.ok) throw new Error('Не удалось сверить раскладку после изменения в другой вкладке.');
      data = await response.json(); positions = {}; refresh();
    }
  }
}
async function load(initial = false) {
  if (sending) { notice('Дождись завершения сохранения.'); return; }
  $('reload').disabled = true; $('create').disabled = true; document.querySelector('.workspace').inert = true;
  try {
    const [response, symbols] = await Promise.all([fetch('/api/technology-editor', { cache: 'no-store' }), atlas ? Promise.resolve(null) : fetch('/assets/topographic-symbols.json')]);
    if (!response.ok) throw new Error(`Редактор недоступен (${response.status}). Нужен сервер с API редактора.`);
    data = await response.json();
    if (symbols) { if (!symbols.ok) throw new Error('Не удалось загрузить атлас'); atlas = await symbols.json(); fillSymbols(); }
    positions = {}; saveError = false; refresh();
    await pinInitialLayout(); $('create').disabled = false; saveState();
    if (initial) {
      // Start on readable foundational knowledge, not on a tiny thumbnail of all species.
      const center = visible.find(n => n.id === 'primitive:gardening') || visible[0];
      if (center) { focusNode(center.id); camera = zoomAt(camera, { x: canvas.clientWidth / 2, y: canvas.clientHeight / 2 }, .85); applyCamera(); }
    }
  } catch (error) { saveError = true; saveState(); notice(error.message, true, true); }
  finally { $('reload').disabled = false; document.querySelector('.workspace').inert = false; }
}
function icon(symbolId) {
  const symbol = atlas?.symbols.find(s => s.id === symbolId);
  const span = el('span', 'node-icon' + (symbol ? '' : ' placeholder'));
  span.draggable = false;
  if (symbol) span.append(symbolSvg(atlas, symbol)); else span.textContent = '◇';
  return span;
}
function fillSymbols() {
  for (const s of atlas.symbols) { const option = el('option', '', s.label); option.value = s.id; $('symbol-select').append(option); }
}
function pendingIds() {
  const ids = new Set(graph.nodes.filter(n => n.manualCount > 0 || n.status === 'missing').map(n => n.id));
  for (const e of graph.edges.filter(e => e.status === 'manual')) { ids.add(e.from); ids.add(e.to); }
  return ids;
}
function filteredNodes() {
  let nodes = graph.nodes;
  if (pendingOnly) {
    const ids = pendingIds();
    // Keep one-hop context: a proposal should not lose the prerequisites around it.
    const context = new Set(ids);
    for (const e of graph.edges) if (ids.has(e.from) || ids.has(e.to)) { context.add(e.from); context.add(e.to); }
    nodes = nodes.filter(n => context.has(n.id));
  }
  return nodes;
}
function refresh() {
  if (!data) return;
  graph = assembleGraph(data.catalog, data.workspace);
  positions = restoreLayout(graph.nodes, graph.edges, data.workspace.positions, positions);
  visible = filteredNodes();
  $('node-count').textContent = graph.nodes.length;
  $('pending-count').textContent = data.workspace.nodes.filter(n => n.status === 'manual').length + data.workspace.edges.filter(e => e.status === 'manual').length + data.workspace.comments.filter(c => c.status === 'manual').length;
  $('graph-title').textContent = pendingOnly ? 'На адаптацию + контекст' : 'Единое дерево';
  $('empty').hidden = visible.length > 0;
  renderLibrary(); renderNodes(); renderInspector();
}
function renderLibrary() {
  const list = $('search-results'); list.replaceChildren();
  const search = $('search').value.trim().toLocaleLowerCase('ru');
  const matches = (search ? graph.nodes : visible).filter(n => !search || `${n.title} ${n.technologyId} ${n.description}`.toLocaleLowerCase('ru').includes(search));
  for (const layer of ['primitive', 'draft', 'missing']) {
    const group = matches.filter(n => n.layer === layer && n.kind !== 'logic');
    if (!group.length) continue;
    list.append(el('div', 'list-heading', ({ primitive: 'Технологии', draft: 'Твои ноды · manual', missing: 'Требуют сверки' })[layer]));
    for (const n of group) {
      const b = button('', () => focusNode(n.id), `list-item${selected === n.id ? ' selected' : ''}`);
      b.title = n.title; b.append(icon(n.symbol), el('span', 'item-title', n.title));
      if (n.manualCount) b.append(el('span', 'manual-dot'));
      list.append(b);
    }
  }
  if (!matches.length) list.append(el('p', 'small-note', 'Ничего не найдено. Можно добавить новую ноду.'));
}
function port(nodeId, direction, type = '') {
  const p = button('', () => {}, `port ${direction}`);
  p.dataset.nodeId = nodeId; p.dataset.direction = direction; p.dataset.linkType = type;
  p.setAttribute('aria-label', `${direction === 'out' ? 'Выход' : 'Вход'} ${type ? LINK_TYPES[type].label + ': ' : ''}${graph.nodes.find(n => n.id === nodeId)?.title}`);
  p.title = `${direction === 'out' ? 'Начать связь' : 'Завершить связь'}${type ? ' · ' + LINK_TYPES[type].label : ''}`;
  if (type) p.style.setProperty('--port-color', LINK_TYPES[type].color);
  p.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); activatePort(p); } });
  return p;
}
function renderNodes() {
  nodeLayer.replaceChildren(); cards.clear();
  const cycles = prerequisiteCycles(graph.nodes, graph.edges);
  for (const n of visible) {
    const isExpanded = expanded.has(n.id);
    const card = el('article', `tech-node${n.kind === 'logic' ? ' logic' : ''}${n.status === 'manual' ? ' manual' : ''}${isExpanded ? ' expanded' : ''}${selected === n.id ? ' selected' : ''}${cycles.has(n.id) ? ' cycle' : ''}`);
    card.dataset.nodeId = n.id; card.setAttribute('aria-label', n.title);
    card.style.left = positions[n.id].x + 'px'; card.style.top = positions[n.id].y + 'px';
    const header = el('div', 'node-header'); header.dataset.dragNode = n.id;
    const title = button(n.title, () => selectNode(n.id), 'node-title');
    const expand = button(isExpanded ? '▾' : '▸', () => {
      isExpanded ? expanded.delete(n.id) : expanded.add(n.id); selected = n.id; selectedEdge = null; refresh();
      if (!isExpanded) {
        const p = positions[n.id];
        camera.x = Math.min(camera.x, canvas.clientWidth - 40 - (p.x + 340) * camera.scale);
        camera.x = Math.max(camera.x, 25 - p.x * camera.scale);
        camera.y = 108 - p.y * camera.scale; applyCamera();
      }
    }, 'expand-button');
    expand.setAttribute('aria-label', `${isExpanded ? 'Свернуть' : 'Развернуть'} ${n.title}`); expand.setAttribute('aria-expanded', String(isExpanded));
    if (n.kind === 'logic') header.append(port(n.id, 'in'), title, port(n.id, 'out'));
    else header.append(port(n.id, 'in'), icon(n.symbol), title, expand, port(n.id, 'out'));
    if (n.manualCount) header.append(el('span', 'node-badge', `manual${n.manualCount > 1 ? ' · ' + n.manualCount : ''}`));
    card.append(header);
    if (isExpanded) {
      const body = el('div', 'node-details');
      body.style.maxHeight = Math.max(180, Math.min(530, (canvas.clientHeight - 210) / camera.scale - 59)) + 'px';
      const ports = el('div', 'typed-ports');
      for (const [type, meta] of Object.entries(LINK_TYPES)) { const row = el('div', 'typed-port-row'); row.append(port(n.id, 'in', type), el('span', '', '← ' + meta.label), el('span', '', meta.label + ' →'), port(n.id, 'out', type)); ports.append(row); }
      body.append(ports); appendDetails(body, n, 'card'); card.append(body);
      body.addEventListener('scroll', scheduleWires);
    }
    nodeLayer.append(card); cards.set(n.id, card);
  }
  scheduleWires();
}
function appendDetails(container, n, surface) {
  container.append(el('h3', '', 'Описание'), el('p', 'details-copy', n.description || 'Пока без описания.'));
  for (const [title, values] of [['Условия возникновения', n.conditions], ['Эффекты внедрения', n.effects]]) {
    container.append(el('h3', '', title)); const list = el('ul', 'facts');
    const nonempty = values.filter(Boolean); for (const text of nonempty.length ? nonempty : ['Нужно уточнить при адаптации.']) list.append(el('li', '', text)); container.append(list);
  }
  const notes = data.workspace.comments.filter(c => resolveId(c.nodeId, data.workspace) === n.id);
  container.append(el('h3', '', `Мысли на адаптацию · ${notes.filter(c => c.status === 'manual').length}`));
  for (const note of notes.filter(c => c.status === 'manual')) container.append(commentView(note));
  const form = el('form', 'comment-form'); const label = el('label', 'field', 'Новый комментарий');
  const text = el('textarea'); text.rows = 3; text.maxLength = 16000; text.placeholder = 'Что изменить, добавить или проверить?'; text.value = noteDrafts[n.id] || '';
  text.setAttribute('aria-label', `Комментарий: ${n.title}${surface === 'card' ? ' (карточка)' : ''}`);
  text.dataset.noteFor = n.id;
  text.addEventListener('input', () => {
    noteDrafts[n.id] = text.value; persistText();
    for (const other of document.querySelectorAll('textarea[data-note-for]')) if (other !== text && other.dataset.noteFor === n.id) other.value = text.value;
  });
  const send = el('button', '', 'Добавить мысль · manual'); send.type = 'submit';
  label.append(text); form.append(label, send, el('p', 'small-note', 'После адаптации комментарий уйдёт в журнал, не исчезнет.'));
  form.addEventListener('submit', async event => {
    event.preventDefault(); const value = text.value.trim(); if (!value) { text.focus(); return; }
    send.disabled = true;
    try {
      await command({ action: 'add-comment', id: crypto.randomUUID(), nodeId: n.id, text: value });
      if ((noteDrafts[n.id] || '').trim() === value) delete noteDrafts[n.id]; persistText(); refresh(); notice('Мысль сохранена и попала в очередь manual.');
    } catch { /* Keep the form and local recovery text. */ } finally { send.disabled = false; }
  });
  container.append(form);
  const journal = el('details', 'journal');
  const reviews = data.workspace.journal.filter(r => r.targetId === n.id);
  const archived = notes.filter(c => c.status === 'adapted');
  journal.append(el('summary', '', `Журнал · этапов: ${reviews.length}`));
  for (const review of reviews.slice().reverse()) {
    const entry = el('div', 'review-entry'); entry.append(el('span', 'small-note', new Date(review.createdAt).toLocaleString('ru')), el('p', '', review.summary), el('p', 'references', review.references.join('\n')));
    for (const part of review.commentProgress || []) {
      entry.append(el('h3', '', 'Реализовано на этом этапе'), el('p', 'details-copy', part.implemented));
      if (part.remaining) entry.append(el('p', 'small-note', 'Остаток после этапа: ' + part.remaining));
      const original = data.workspace.comments.find(c => c.id === part.commentId);
      if (original) { const reference = el('details'); reference.append(el('summary', '', 'Исходная мысль'), el('p', 'details-copy', original.text)); entry.append(reference); }
    }
    for (const edge of review.edgeAdaptations || []) {
      const original = data.workspace.edges.find(item => item.id === edge.edgeId);
      if (original) entry.append(el('p', 'small-note', `Связь нормализована: ${original.type} → ${edge.implementedType}.`));
    }
    for (const id of review.nodeIds) {
      const draft = data.workspace.nodes.find(d => d.id === id);
      if (draft) { const original = el('details'); original.append(el('summary', '', 'Исходная нода: ' + draft.title), el('p', 'details-copy', [draft.description, draft.conditions, draft.effects].filter(Boolean).join('\n\n'))); entry.append(original); }
    }
    journal.append(entry);
  }
  for (const c of archived) journal.append(commentView(c));
  if (!reviews.length && !archived.length) journal.append(el('p', 'small-note', 'Здесь сохранятся обработанные мысли и результаты адаптаций.'));
  container.append(journal);
}
function commentView(note) {
  const div = el('div', `comment${note.status === 'adapted' ? ' archived' : ''}`);
  const partial = note.status === 'manual' && note.remainingText != null;
  div.append(el('small', '', `${partial ? 'manual · частично реализовано' : note.status} · ${new Date(note.createdAt).toLocaleString('ru')}`), el('p', '', partial ? 'Осталось: ' + note.remainingText : note.text));
  if (partial) { const original = el('details'); original.append(el('summary', '', 'Исходная мысль целиком'), el('p', '', note.text)); div.append(original); }
  return div;
}
function renderInspector() {
  const panel = $('inspector');
  if (!selected && !selectedEdge) return;
  panel.replaceChildren();
  panel.append(button('× Закрыть', () => {
    selected = selectedEdge = null;
    panel.replaceChildren(el('div', 'inspector-intro', 'Выбери ноду или связь на схеме.')); renderLibrary(); renderNodes();
  }, 'text-button'));
  if (selectedEdge) {
    const e = graph.edges.find(e => e.id === selectedEdge); if (!e) return;
    const meta = LINK_TYPES[e.type]; panel.append(el('h2', '', meta.label), el('p', 'details-copy', meta.description));
    for (const [label, id] of [['От', e.from], ['К', e.to]]) panel.append(button(`${label}: ${graph.nodes.find(n => n.id === id)?.title ?? id}`, () => focusNode(id), 'list-item'));
    panel.append(el('span', 'tag ' + (e.status === 'manual' ? 'manual' : ''), e.status === 'source' ? 'Исходный каталог' : e.status));
    panel.append(el('p', 'small-note', e.status === 'manual' ? 'Предложение. Не меняет условия открытия технологий до адаптации.' : 'Утверждённая связь. Изменения предлагай комментарием к технологии.'));
    if (e.status === 'manual') panel.append(button('Отозвать предложенную связь', async () => { try { await command({ action: 'withdraw-edge', id: e.id }); selectedEdge = null; panel.replaceChildren(el('div', 'inspector-intro', 'Связь отозвана; запись сохранена в экспорте.')); refresh(); } catch { /* Reported centrally. */ } }));
    return;
  }
  const n = graph.nodes.find(n => n.id === selected); if (!n) return;
  panel.append(el('span', 'eyebrow', DOMAINS[n.domain] || n.domain));
  const heading = el('div', 'inspector-heading'); heading.append(icon(n.symbol), el('h2', '', n.title)); panel.append(heading);
  panel.append(el('div', 'source-id', `${n.id}\n${n.source || ''}`), el('span', `tag${n.status === 'manual' ? ' manual' : ''}`, n.status === 'manual' ? 'manual · ожидает адаптации' : n.layer === 'missing' ? 'Нет исходного определения' : 'Единый каталог'));
  if (n.status === 'manual') panel.append(button('Редактировать черновик', () => openNodeDialog(n.id), 'text-button'));
  appendDetails(panel, n, 'inspector');
  panel.append(el('h3', '', 'Связи'));
  const links = el('div', 'link-list');
  for (const e of graph.edges.filter(e => e.from === n.id || e.to === n.id)) {
    const target = e.from === n.id ? e.to : e.from;
    links.append(button(`${e.from === n.id ? '→' : '←'} ${LINK_TYPES[e.type].label}: ${graph.nodes.find(x => x.id === target)?.title ?? target}${e.status === 'manual' ? ' · manual' : ''}`, () => { selectedEdge = e.id; selected = null; renderInspector(); drawWires(); }));
  }
  if (!links.children.length) links.append(el('p', 'small-note', 'Пока нет связей. Соедини выход этой ноды с входом другой.'));
  panel.append(links);
}
function selectNode(id) { const changed = selected !== id || selectedEdge; selected = id; selectedEdge = null; renderLibrary(); renderInspector(); if (changed) $('inspector').scrollTop = 0; for (const [key, card] of cards) card.classList.toggle('selected', key === id); drawWires(); }
function focusNode(id) {
  if (!graph.nodes.some(n => n.id === id)) return;
  if (!visible.some(n => n.id === id)) { pendingOnly = false; $('manual-filter').setAttribute('aria-pressed', 'false'); refresh(); }
  selectNode(id); const p = positions[id]; camera = { x: canvas.clientWidth / 2 - (p.x + 143), y: canvas.clientHeight / 2 - (p.y + 30), scale: 1 }; applyCamera();
}
function applyCamera() {
  world.style.transform = `translate(${camera.x}px,${camera.y}px) scale(${camera.scale})`;
  const grid = 24 * camera.scale; canvas.style.backgroundSize = `${grid}px ${grid}px`; canvas.style.backgroundPosition = `${camera.x}px ${camera.y}px`;
  $('zoom').textContent = Math.round(camera.scale * 100) + '%';
}
function fit(nodes = visible) {
  if (!nodes.length) return;
  const minX = Math.min(...nodes.map(n => positions[n.id].x)), maxX = Math.max(...nodes.map(n => positions[n.id].x + (cards.get(n.id)?.offsetWidth || 286)));
  const minY = Math.min(...nodes.map(n => positions[n.id].y)), maxY = Math.max(...nodes.map(n => positions[n.id].y + (cards.get(n.id)?.offsetHeight || 59)));
  const scale = Math.max(.2, Math.min(1, (canvas.clientWidth - 95) / Math.max(1, maxX - minX), (canvas.clientHeight - 205) / Math.max(1, maxY - minY)));
  camera = { scale, x: canvas.clientWidth / 2 - (minX + maxX) / 2 * scale, y: (canvas.clientHeight + 10) / 2 - (minY + maxY) / 2 * scale }; applyCamera();
}
function portPosition(id, direction, type = '') {
  const card = cards.get(id); if (!card) return null;
  const typed = expanded.has(id) && type ? [...card.querySelectorAll('.typed-ports .port')].find(p => p.dataset.direction === direction && p.dataset.linkType === type) : null;
  const port = typed || card.querySelector(`.node-header .port.${direction}`);
  const rect = port.getBoundingClientRect(), bounds = canvas.getBoundingClientRect();
  return screenToWorld({ x: rect.left + rect.width / 2 - bounds.left, y: rect.top + rect.height / 2 - bounds.top }, camera);
}
function svgEl(tag, attrs = {}) { const element = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const [key, value] of Object.entries(attrs)) element.setAttribute(key, value); return element; }
function scheduleWires() { cancelAnimationFrame(renderFrame); renderFrame = requestAnimationFrame(drawWires); }
function drawWires() {
  if (!graph) return;
  wires.replaceChildren();
  const defs = svgEl('defs');
  for (const [type, meta] of Object.entries(LINK_TYPES)) { const marker = svgEl('marker', { id: 'arrow-' + type, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 6, markerHeight: 6, orient: 'auto' }); marker.append(svgEl('path', { d: 'M 1 1 L 9 5 L 1 9', fill: 'none', stroke: meta.color, 'stroke-width': 1.5 })); defs.append(marker); }
  wires.append(defs);
  for (const e of graph.edges) {
    const a = portPosition(e.from, 'out', e.type), b = portPosition(e.to, 'in', e.type); if (!a || !b) continue;
    const parallel = graph.edges.filter(other => other.from === e.from && other.to === e.to).sort((x, y) => x.type.localeCompare(y.type));
    const lane = (parallel.findIndex(other => other.id === e.id) - (parallel.length - 1) / 2) * 38;
    const d = edgePath(a, b, lane); const related = selectedEdge === e.id || e.from === selected || e.to === selected;
    const hit = svgEl('path', { d, class: 'wire-hit', 'aria-label': `${LINK_TYPES[e.type].label}: ${e.from} → ${e.to}` });
    hit.addEventListener('click', () => { selectedEdge = e.id; selected = null; renderInspector(); renderLibrary(); for (const card of cards.values()) card.classList.remove('selected'); drawWires(); });
    const stroke = svgEl('path', { d, class: `wire${e.status === 'manual' ? ' manual' : ''}${related ? ' related' : selected || selectedEdge ? ' dim' : ''}`, stroke: LINK_TYPES[e.type].color, 'marker-end': `url(#arrow-${e.type})` });
    const title = svgEl('title'); title.textContent = LINK_TYPES[e.type].description; hit.append(title); wires.append(hit, stroke);
  }
  if (connection) {
    const a = portPosition(connection.from, 'out', connection.type);
    if (a) wires.append(svgEl('path', { d: edgePath(a, connection.point || { x: a.x + 70, y: a.y }), class: 'wire-preview' }));
  }
}
function point(event) { const r = canvas.getBoundingClientRect(); return { x: event.clientX - r.left, y: event.clientY - r.top }; }
function activatePort(p) {
  if (p.dataset.direction === 'out') {
    connection = { from: p.dataset.nodeId, type: p.dataset.linkType || $('link-type').value }; drawWires();
    notice('Выбери вход другой ноды. Тип: ' + LINK_TYPES[connection.type].label + '. Esc — отмена.', false, true);
  } else if (connection) completeConnection(p);
  else notice('Начни связь с выходной точки справа у исходной ноды.');
}
async function completeConnection(p) {
  if (!connection) return;
  const proposal = connection; const to = p.dataset.nodeId, portType = p.dataset.linkType;
  if (to === proposal.from) { notice('Связь должна вести к другой ноде.', true); return; }
  if (portType && portType !== proposal.type) { notice('Типы портов не совпадают. Выбери подходящий вход или общую точку в заголовке.', true); return; }
  connection = null; drawWires();
  try {
    await command({ action: 'add-edge', edge: { id: crypto.randomUUID(), from: proposal.from, to, type: proposal.type } }); refresh();
    const cycles = prerequisiteCycles(graph.nodes, graph.edges);
    notice(cycles.size ? 'Связь сохранена как manual. Есть цикл необходимых условий: отмечен красной обводкой, нужно обсудить при адаптации.' : 'Связь сохранена как manual.');
  } catch { /* Reported centrally. */ }
}
canvas.addEventListener('selectstart', preventGraphSelection);
canvas.addEventListener('dragstart', preventGraphNativeDrag);
canvas.addEventListener('pointerdown', event => {
  if (event.button !== 0 && event.button !== 1) return;
  const p = event.target.closest('.port');
  if (p) { event.preventDefault(); if (p.dataset.direction === 'out') activatePort(p); gesture = { kind: 'port', pointerId: event.pointerId }; canvas.setPointerCapture(event.pointerId); return; }
  if (event.target.closest('.node-details,.expand-button,.wire-hit')) return;
  const header = event.target.closest('[data-drag-node]');
  if (!header && event.target.closest('.tech-node')) return;
  // Prevent native selection/autoscroll as soon as a pan or node move starts.
  // Inputs, selectable descriptions, expand buttons and links returned above.
  event.preventDefault(); canvas.focus({ preventScroll: true });
  const start = point(event);
  gesture = header && event.button !== 1 ? { kind: 'node', id: header.dataset.dragNode, start, original: { ...positions[header.dataset.dragNode] }, moved: false, pointerId: event.pointerId }
    : { kind: 'pan', start, original: { ...camera }, pointerId: event.pointerId };
  canvas.setPointerCapture(event.pointerId);
});
canvas.addEventListener('pointermove', event => {
  const current = point(event);
  if (connection) { connection.point = screenToWorld(current, camera); scheduleWires(); }
  if (!gesture) return;
  if (gesture.kind === 'pan') { camera.x = gesture.original.x + current.x - gesture.start.x; camera.y = gesture.original.y + current.y - gesture.start.y; applyCamera(); }
  if (gesture.kind === 'node') {
    const dx = current.x - gesture.start.x, dy = current.y - gesture.start.y;
    if (Math.abs(dx) + Math.abs(dy) < 4 && !gesture.moved) return;
    gesture.moved = true;
    positions[gesture.id] = snapNodePosition({ x: gesture.original.x + dx / camera.scale, y: gesture.original.y + dy / camera.scale });
    const card = cards.get(gesture.id); card.style.left = positions[gesture.id].x + 'px'; card.style.top = positions[gesture.id].y + 'px'; scheduleWires();
  }
});
canvas.addEventListener('pointerup', async event => {
  if (!gesture) return;
  const finished = gesture; gesture = null;
  if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
  if (finished.kind === 'port') {
    const p = document.elementFromPoint(event.clientX, event.clientY)?.closest('.port');
    if (p?.dataset.direction === 'in') activatePort(p); return;
  }
  if (finished.kind === 'node') {
    selectNode(finished.id);
    if (finished.moved) try { await command({ action: 'move-nodes', positions: { [finished.id]: positions[finished.id] } }); } catch { /* Keep the visible position for export/retry. */ }
  }
});
canvas.addEventListener('pointercancel', () => { if (gesture?.kind === 'node') { positions[gesture.id] = gesture.original; renderNodes(); } gesture = null; });
canvas.addEventListener('wheel', event => {
  if (event.target.closest('textarea,select,.node-details')) return;
  event.preventDefault(); camera = zoomAt(camera, point(event), camera.scale * Math.exp(-event.deltaY * .0015)); applyCamera();
}, { passive: false });
canvas.addEventListener('keydown', event => {
  if (event.target !== canvas) return;
  const delta = { ArrowLeft: [60, 0], ArrowRight: [-60, 0], ArrowUp: [0, 60], ArrowDown: [0, -60] }[event.key];
  if (delta) { event.preventDefault(); camera.x += delta[0]; camera.y += delta[1]; applyCamera(); }
});
document.addEventListener('keydown', event => { if (event.key === 'Escape') { connection = null; gesture = null; $('notice').hidden = true; drawWires(); } });

function openNodeDialog(id = null) {
  editingId = id; const form = $('node-form'); form.reset(); $('form-error').textContent = '';
  const source = id ? data.workspace.nodes.find(n => n.id === id) : formDraft;
  for (const key of ['title', 'description', 'conditions', 'effects', 'domain', 'symbol']) if (source?.[key] != null) form.elements.namedItem(key).value = source[key];
  $('dialog-title').textContent = id ? 'Редактировать черновик' : 'Новая технология'; $('submit-node').textContent = id ? 'Сохранить черновик' : 'Добавить в сеть';
  $('node-dialog').showModal(); form.elements.namedItem('title').focus();
}
$('node-form').addEventListener('input', () => { if (!editingId) { formDraft = Object.fromEntries(new FormData($('node-form'))); persistText(); } });
$('node-form').addEventListener('submit', async event => {
  event.preventDefault(); $('submit-node').disabled = true; $('form-error').textContent = '';
  const values = Object.fromEntries(new FormData(event.currentTarget)); const id = editingId || 'draft:' + crypto.randomUUID();
  try {
    await command({ action: editingId ? 'edit-node' : 'create-node', node: { ...values, title: values.title.trim(), symbol: values.symbol || null, id } });
    if (!editingId) {
      positions[id] = vacantPosition(screenToWorld({ x: canvas.clientWidth / 2 - 143 * camera.scale, y: canvas.clientHeight / 2 }, camera), positions);
      // Creation already succeeded; do not duplicate the draft if layout persistence fails.
      try { await command({ action: 'move-nodes', positions: { [id]: positions[id] } }); } catch { /* Keep the node and local position. */ }
      formDraft = null; persistText();
    }
    $('node-dialog').close(); refresh(); focusNode(id);
  } catch (error) { $('form-error').textContent = error.message; } finally { $('submit-node').disabled = false; }
});
$('close-dialog').addEventListener('click', () => $('node-dialog').close());
$('create').addEventListener('click', () => openNodeDialog());
$('reload').addEventListener('click', () => load());
$('search').addEventListener('input', () => { if (graph) renderLibrary(); });
$('manual-filter').addEventListener('click', () => { pendingOnly = !pendingOnly; $('manual-filter').setAttribute('aria-pressed', String(pendingOnly)); refresh(); fit(); });
$('fit').addEventListener('click', () => fit());
for (const [id, factor] of [['zoom-in', 1.25], ['zoom-out', .8]]) $(id).addEventListener('click', () => { camera = zoomAt(camera, { x: canvas.clientWidth / 2, y: canvas.clientHeight / 2 }, camera.scale * factor); applyCamera(); });
$('export').addEventListener('click', () => {
  if (!data) return;
  const blob = new Blob([JSON.stringify({ ...data, localRecovery: { notes: noteDrafts, form: formDraft, positions } }, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob), a = el('a'); a.href = url; a.download = 'worldgen-technology-network.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
});
for (const [value, meta] of Object.entries(LINK_TYPES)) { const option = el('option', '', meta.label); option.value = value; $('link-type').append(option); }
new ResizeObserver(() => { applyCamera(); scheduleWires(); }).observe(canvas);
window.addEventListener('beforeunload', event => { if (sending || saveError) { event.preventDefault(); event.returnValue = ''; } });
await load(true);
