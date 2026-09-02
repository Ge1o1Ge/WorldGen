import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { FACE_NAMES,facePoint,locateFace,blend,createSurfaceSampler,contourSegments,joinSegments,belowThresholdPolygon,symbolSpacing,symbolAnchor } from "../visualizer/sphere-cartography.js";

test("sphere cartography uses the same face mapping, including ghost samples across seams",()=>{
  for(const face of FACE_NAMES) {
    const mapped=locateFace(facePoint(face,18,23,64));
    assert.equal(mapped.face,face);
    assert.ok(Math.abs((mapped.u+1)*32-.5-18)<1e-9);
    assert.notEqual(locateFace(facePoint(face,-1,23,64)).face,face);
  }
});
test("surface interpolation is continuous and ownership never changes simulation data",()=>{
  assert.equal(blend([0,10,20,10],.5,.5),10);
  const sampler=createSurfaceSampler({size:16,stride:1,origin:0,read:(_face,x,y)=>
    ({elevation:x+y,forest:.5,moisture:.8,owner:x<8?0:1,biome:4,exact:true})});
  const point={face:"PositiveZ",u:0,v:0};
  const sample=sampler(point,true);
  assert.equal(sample.elevation,15);
  assert.equal(sample.claims.get(0),.5);
  assert.equal(sample.claims.get(1),.5);
});
test("contour extracts a linear height field and joins fragments",()=>{
  const segments=contourSegments([0,10,20,0,10,20,0,10,20],3,3,5);
  const paths=joinSegments(segments);
  assert.equal(paths.length,1);
  assert.equal(paths[0].length,3);
  assert.ok(paths[0].every(point=>point[0]===.5));
  assert.equal(contourSegments([NaN,10,0,20],2,2,5).length,0);
});
test("saddle contour is resolved without crossing, loops remain closed",()=>{
  const segments=contourSegments([2,-1,-1,2],2,2,0);
  assert.equal(segments.length,2);
  const loop=joinSegments([[[0,0],[1,0]],[[1,1],[0,1]],[[0,1],[0,0]],[[1,0],[1,1]]])[0];
  assert.deepEqual(loop[0],loop.at(-1));
  assert.equal(loop.length,5);
});

test("shore saddle turns around the higher corners and keeps its centre on the lower diagonal",()=>{
  const edge=([x,y])=>y===0?"top":x===1?"right":y===1?"bottom":"left";
  const pairs=values=>contourSegments(values,2,2,0)
    .map(segment=>segment.map(edge).sort().join("-")).sort();
  const deepWet=contourSegments([4,-1,-1,4],2,2,0);
  assert.equal(deepWet.length,2);
  assert.deepEqual(pairs([4,-1,-1,4]),["bottom-left","right-top"]);
  assert.deepEqual(pairs([1,-4,-4,1]),["bottom-right","left-top"]);
});
test("vegetation anchors are independent of camera pan and remain fixed within zoom band",()=>{
  assert.equal(symbolSpacing(9),symbolSpacing(12));
  assert.deepEqual(symbolAnchor("PositiveZ",2,3,symbolSpacing(9),123),symbolAnchor("PositiveZ",2,3,symbolSpacing(12),123));
  assert.notDeepEqual(symbolAnchor("PositiveZ",2,3,4,123),symbolAnchor("NegativeZ",2,3,4,123));
});

test("river clipping stops at interpolated shores, not cell edges",()=>{
  assert.deepEqual(belowThresholdPolygon([{x:0,y:0,value:-1},{x:2,y:0,value:1},{x:0,y:2,value:1}]),[[0,0],[1,0],[0,1]]);
  assert.equal(belowThresholdPolygon([{x:0,y:0,value:1},{x:2,y:0,value:1},{x:0,y:2,value:1}]).length,0);
});

test("editable atlas has unique paths, palette roles and every rendered feature",()=>{
  const atlas=JSON.parse(readFileSync(new URL("../visualizer/assets/topographic-symbols.json",import.meta.url),"utf8"));
  assert.equal(atlas.version,1);
  const ids=new Set(atlas.symbols.map(symbol=>symbol.id));
  assert.equal(ids.size,atlas.symbols.length);
  for(const id of ["conifer","deciduous","wetland","grass","rock","orchard","field","building","mill","fort","bridge","road","trail","river","contour","boundary"]) assert.ok(ids.has(id));
  for(const symbol of atlas.symbols) {
    assert.match(symbol.path,/^M/);
    assert.match(atlas.palette[symbol.role],/^#[0-9a-f]{6}$/i);
    assert.equal(typeof symbol.fill,"boolean");
  }
});
