import test from "node:test";
import assert from "node:assert/strict";
import {wildlifeZonePoints,wildlifeSummary} from "../visualizer/sphere-wildlife.js";
import {buildingGlyph,buildingAnchor} from "../visualizer/settlement-symbols.js";

test("wildlife ranges stay closed on the sphere, including face seams",()=>{
  for(const face of ["PositiveX","NegativeX","PositiveY","NegativeZ"]){
    const group={face,x:0,y:1023,radiusCells:3};
    const points=wildlifeZonePoints(group,1024);
    assert.equal(points.length,33);
    for(const p of points)assert.ok(Math.abs(Math.hypot(p.x,p.y,p.z)-1)<1e-10);
    assert.ok(Math.hypot(points[0].x-points[32].x,points[0].y-points[32].y,points[0].z-points[32].z)<1e-10);
    assert.deepEqual(points,wildlifeZonePoints(group,1024));
  }
});
test("wildlife overview is observer-only and gardens use a whole-cell field glyph",()=>{
  assert.match(wildlifeSummary([{alert:1},{alert:0}]),/наблюдатель: 2.*1 встревожены/);
  const garden={kind:"garden",face:"PositiveX",x:10,y:10,slot:0,status:"active"};
  assert.equal(buildingGlyph(garden),"field");
  assert.deepEqual(buildingAnchor(garden),garden);
});
