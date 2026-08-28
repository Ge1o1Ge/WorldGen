import test from "node:test";
import assert from "node:assert/strict";
import {buildingGlyph,buildingAnchor} from "../visualizer/settlement-symbols.js";

test("settlement glyphs show actual building lifecycle",()=>{
  assert.equal(buildingGlyph({kind:"house",status:"building"}),"construction");
  assert.equal(buildingGlyph({kind:"house",status:"active"}),"house");
  assert.equal(buildingGlyph({kind:"well",status:"active"}),"well");
  assert.equal(buildingGlyph({kind:"house",status:"abandoned"}),"ruin");
});
test("four house anchors are distinct, inside their cell and independent of camera",()=>{
  const anchors=Array.from({length:4},(_,slot)=>buildingAnchor({face:"PositiveZ",x:40,y:50,slot}));
  assert.equal(new Set(anchors.map(p=>`${p.x}:${p.y}`)).size,4);
  for(const p of anchors){assert.ok(Math.abs(p.x-40)<.5);assert.ok(Math.abs(p.y-50)<.5);}
  assert.deepEqual(buildingAnchor({x:40,y:50,slot:-1}),{x:40,y:50,slot:-1});
});
