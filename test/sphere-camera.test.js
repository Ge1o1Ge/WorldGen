import test from "node:test";
import assert from "node:assert/strict";
import { SphereCamera, MAX_SPHERE_ZOOM } from "../visualizer/sphere-camera.js";

const near = (a, b) => assert.ok(Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z) < 1e-9);
test("сферическая камера: захваченная точка следует вправо и вниз за курсором", () => {
  const camera = new SphereCamera();
  for (const zoom of [1, 8, 64]) {
    camera.zoom = zoom;
    const before = camera.worldAt(400, 350, 800, 700);
    const drag = camera.beginDrag(400, 350, 800, 700);
    camera.drag(drag, 450, 380, 800, 700);
    near(before, camera.worldAt(450, 380, 800, 700));
  }
});
test("зум сохраняет точку под курсором и достигает отдельных зон", () => {
  const camera = new SphereCamera();
  const before = camera.worldAt(470, 300, 800, 700);
  camera.zoomAt(64, 470, 300, 800, 700);
  near(before, camera.worldAt(470, 300, 800, 700));
  assert.equal(camera.zoom, 64);
  camera.zoomAt(1000, 470, 300, 800, 700);
  assert.equal(camera.zoom, MAX_SPHERE_ZOOM);
});
test("фокусировка и обратная проекция работают у полюсов", () => {
  const camera = new SphereCamera();
  for (const point of [{x:0,y:1,z:0}, {x:0,y:-1,z:0}, {x:1,y:0,z:0}]) {
    camera.focus(point);
    near(camera.toWorld(0, 0, 1), point);
    near(camera.toView(point), {x:0,y:0,z:1});
  }
});
