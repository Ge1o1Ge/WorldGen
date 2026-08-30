import test from "node:test";
import assert from "node:assert/strict";
import { dampZoom, SphereCamera, MAX_SPHERE_ZOOM } from "../visualizer/sphere-camera.js";

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

test("плавный зум монотонно догоняет цель и сохраняет мировую точку под курсором",()=>{
  const camera=new SphereCamera(),x=470,y=300,width=800,height=700;
  const anchored=camera.worldAt(x,y,width,height);
  let previous=camera.zoom;
  for(let frame=0;frame<40;frame++){
    const next=dampZoom(camera.zoom,8,16);
    assert.ok(next>previous&&next<=8);previous=next;
    camera.zoomAt(next,x,y,width,height);near(anchored,camera.worldAt(x,y,width,height));
  }
  assert.ok(Math.abs(Math.log(camera.zoom/8))<.001);
  assert.equal(dampZoom(3,3,16),3);
});
