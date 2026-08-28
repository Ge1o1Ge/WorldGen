import test from "node:test";
import assert from "node:assert/strict";
import { WorldGeometryCache,buildWorldTile,worldTileSymbols,roundWorldPath,roundSpherePath,clipWorldPath,landMaskAlpha } from "../visualizer/sphere-world-geometry.js";

const sample=(cell)=>({elevation:100+cell.x+cell.y,forest:.8,moisture:.5,lakeDepth:0,biome:4,claims:new Map([[0,1]])});
test("pan only reprojects cached world geometry; LOD and data are explicit keys",()=>{
  const cache=new WorldGeometryCache(3),build=()=>buildWorldTile({face:"PositiveZ",tx:0,ty:0,size:64,step:4,sample,cityCount:1,seaLevel:0});
  const first=cache.get("tile:4",1,build);
  for(let pan=0;pan<10;pan++)assert.equal(cache.get("tile:4",1,build),first);
  assert.equal(cache.builds,1);assert.ok(first.length>0);
  assert.notEqual(cache.get("tile:4",2,build),first);assert.equal(cache.builds,2);
});
test("symbols are selected in world space, independent of screen collisions and viewport crop",()=>{
  const options={face:"PositiveZ",tx:0,ty:0,size:64,zoom:8,seed:5,sample,settlements:[],seaLevel:0};
  const symbols=worldTileSymbols(options);
  assert.ok(symbols.length>20);
  assert.deepEqual(symbols,worldTileSymbols({...options,zoom:12}));
  assert.equal(worldTileSymbols({...options,sample:cell=>({...sample(cell),lakeDepth:2})}).length,0);
});
test("rounding before tile clipping keeps a shared endpoint between adjacent tiles",()=>{
  const path=roundWorldPath([[0,0],[15,12],[32,8],[48,15],[64,0]]);
  const left=clipWorldPath(path,{x0:0,y0:-1,x1:32,y1:20}).at(-1).at(-1);
  const right=clipWorldPath(path,{x0:32,y0:-1,x1:64,y1:20})[0][0];
  assert.deepEqual(left,right);
});
test("the exact same raster water classification masks all river ink, including narrow lakes",()=>{
  for(const depth of [1.001,2,30])assert.equal(landMaskAlpha(100,depth,62),0);
  assert.equal(landMaskAlpha(61,0,62),0);
  assert.equal(landMaskAlpha(100,0,62),255);
  assert.equal(landMaskAlpha(100,30,62,false),255);
});

test("river smoothing stays on the sphere and preserves shared reach endpoints",()=>{
  const point=(x,y,z)=>{const length=Math.hypot(x,y,z);return [x/length,y/length,z/length];};
  const points=[point(1,0,0),point(1,.2,.1),point(1,.3,.3),point(1,.5,.4)];
  const original=structuredClone(points),rounded=roundSpherePath(points);
  assert.deepEqual(points,original);
  assert.deepEqual(rounded[0],points[0]);
  assert.deepEqual(rounded.at(-1),points.at(-1));
  assert.ok(rounded.length>points.length);
  for(const p of rounded)assert.ok(Math.abs(Math.hypot(...p)-1)<1e-12);
  assert.deepEqual(roundSpherePath(points.slice(0,2)),points.slice(0,2));
});
