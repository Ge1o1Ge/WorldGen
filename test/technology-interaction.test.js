import test from 'node:test';
import assert from 'node:assert/strict';
import { preventGraphSelection, preventGraphNativeDrag } from '../visualizer/technology-interaction.js';

function eventFor(type, ancestors = [], textNode = false) {
  const element = { closest: selectors => selectors.split(',').some(s => ancestors.includes(s.trim())) ? element : null };
  const event = new Event(type, { cancelable: true });
  Object.defineProperty(event, 'target', { value: textNode ? { nodeType: 3, parentElement: element } : element });
  return event;
}
test('native selection and image drag are cancelled on graph background, titles, ports and symbols', () => {
  for (const ancestor of ['#canvas', '.node-header', '.node-icon', 'svg', '.port']) {
    const selection = eventFor('selectstart', [ancestor]); preventGraphSelection(selection); assert.equal(selection.defaultPrevented, true);
    const drag = eventFor('dragstart', [ancestor]); preventGraphNativeDrag(drag); assert.equal(drag.defaultPrevented, true);
  }
});
test('comments, inputs and editable text retain native editing and selection', () => {
  for (const field of ['input', 'textarea', '[contenteditable="true"]']) {
    for (const textNode of [false, true]) {
      const selection = eventFor('selectstart', [field], textNode); preventGraphSelection(selection); assert.equal(selection.defaultPrevented, false);
      const drag = eventFor('dragstart', [field], textNode); preventGraphNativeDrag(drag); assert.equal(drag.defaultPrevented, false);
    }
  }
});
test('expanded descriptions can be copied without becoming native draggable fragments', () => {
  const selection = eventFor('selectstart', ['.node-details'], true); preventGraphSelection(selection); assert.equal(selection.defaultPrevented, false);
  const drag = eventFor('dragstart', ['.node-details'], true); preventGraphNativeDrag(drag); assert.equal(drag.defaultPrevented, true);
});
