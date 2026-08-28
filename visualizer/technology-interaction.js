// Native text/image dragging must not compete with the editor's pointer gestures.
// Keep text selection/copying in expanded descriptions and editing in form fields.
const textFields = 'input, textarea, [contenteditable="true"]';
function closest(event, selector) {
  const target = event.target?.nodeType === 3 ? event.target.parentElement : event.target;
  return target?.closest?.(selector);
}
export function preventGraphSelection(event) {
  if (!closest(event, `${textFields}, .node-details`)) event.preventDefault();
}
export function preventGraphNativeDrag(event) {
  if (!closest(event, textFields)) event.preventDefault();
}
