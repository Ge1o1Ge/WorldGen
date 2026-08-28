import test from "node:test";
import assert from "node:assert/strict";
import { SphereMapData, structureTiles } from "../visualizer/sphere-map-data.js";
import { SphereChunkCache } from "../visualizer/sphere-chunks.js";
import { buildWorldTile } from "../visualizer/sphere-world-geometry.js";
import { FACE_NAMES, facePoint, locateFace, createSurfaceSampler } from "../visualizer/sphere-cartography.js";

const options={worldId:"world-a",faceSize:64,chunkSize:32,faces:["PositiveZ","PositiveX"]};
const cell={face:"PositiveZ",x:4,y:5};
const base={elevation:120,forest:.8,owner:-1,influence:0,exact:true};
const update=(extra={})=>({worldId:options.worldId,revision:1,claimsRevision:1,forest:[],claims:[],settlements:[],...extra});

test("forest overlays leave immutable terrain intact and removals restore procedural forest",()=>{
  const data=new SphereMapData(options);
  data.apply(update({forest:[{...cell,forest:.2}]}));
  assert.deepEqual(data.read(cell,base),{...base,forest:.2});
  assert.equal(base.forest,.8);
  data.apply(update({revision:2,claims:null,settlements:null}));
  assert.deepEqual(data.read(cell,base),base);
});

test("new days without map changes do not invalidate any tile; forest changes stay local",()=>{
  const data=new SphereMapData(options);
  data.apply(update());
  data.apply(update({revision:2,claims:null,settlements:null}));
  assert.equal(data.tileVersions.size,0);
  data.apply(update({revision:3,forest:[{...cell,forest:0}],claims:null,settlements:null}));
  assert.equal(data.version(data.tile(cell)),1);
  assert.equal(data.version(data.tile({...cell,x:40})),0);
  assert.equal(data.version(data.tile({...cell,face:"PositiveX"})),0);
  data.apply(update({revision:4,forest:[{...cell,forest:0}],claims:null,settlements:null}));
  assert.equal(data.version(data.tile(cell)),1);
});

test("claims are retained when omitted and removed only by a replacement snapshot",()=>{
  const data=new SphereMapData(options);
  data.apply(update({claims:[{...cell,owner:0,influence:.7}]}));
  data.apply(update({revision:2,claims:null,settlements:null}));
  assert.equal(data.read(cell,base).owner,0);
  data.apply(update({revision:3,claimsRevision:2}));
  assert.equal(data.read(cell,base).owner,-1);
  assert.equal(data.read(cell,base).influence,0);
});

test("out-of-order, incomplete and foreign-world responses cannot erase the current map",()=>{
  const data=new SphereMapData(options);
  data.apply(update({revision:10,forest:[{...cell,forest:.3}]}));
  const forest=data.forest,versions=[...data.tileVersions];
  assert.equal(data.apply(update({revision:9})).accepted,false);
  assert.throws(()=>data.apply(update({revision:11,claimsRevision:2,claims:null,settlements:null})),/полный слой/);
  assert.throws(()=>data.apply(update({revision:12,worldId:"other"})),/Мир на сервере/);
  assert.equal(data.forest,forest);
  assert.deepEqual([...data.tileVersions],versions);
  assert.equal(data.revision,10);
});

test("building geometry depends on footprints, not population or progress",()=>{
  const data=new SphereMapData(options);
  const before=[{id:"city",buildings:[{...cell,id:"house",buildingTypeId:"house",capacityUnits:1,residents:10,status:"construction"}],usedLands:[]}];
  const after=structuredClone(before);
  Object.assign(after[0].buildings[0],{residents:25,status:"active",laborDone:60});
  assert.equal(structureTiles(before,after,data).size,0);
  after[0].buildings[0].x=40;
  assert.deepEqual(structureTiles(before,after,data),new Set([0,1]));
  assert.deepEqual(structureTiles(before,[],data),new Set([0]));
  const lands=[{id:"city",buildings:[],usedLands:[{...cell,id:"field",usage:1}]}];
  const abandoned=structuredClone(lands);abandoned[0].usedLands[0].usage=0;
  assert.deepEqual(structureTiles(lands,abandoned,data),new Set([0]));
});

test("household expectations and food observations never invalidate map geometry",()=>{
  const data=new SphereMapData(options);data.apply(update());
  const settlements=[{id:"city",buildings:[{...cell,id:"house",buildingTypeId:"house",capacityUnits:1}],usedLands:[]}];
  const changed=structuredClone(settlements);
  changed[0].wellbeing={satisfaction:.7,consumedToday:{fish:.04},households:{house:{expectedRest:.5}}};
  const result=data.apply(update({revision:2,claims:null,settlements:null,wellbeing:changed[0].wellbeing}));
  assert.equal(result.changedTiles.size,0);assert.equal(data.tileVersions.size,0);
  assert.equal(structureTiles(settlements,changed,data).size,0);
});

test("an exact chunk arriving during simulation updates stays usable without abort or refetch",async()=>{
  const data=new SphereMapData(options);data.apply(update());
  let finish,signal,requests=0;
  const cache=new SphereChunkCache({fetchChunk:(_,s)=>{requests++;signal=s;return new Promise(resolve=>{finish=resolve;});}});
  cache.setDesired([0]);await Promise.resolve();
  data.apply(update({revision:2,forest:[{...cell,forest:.15}],claims:null,settlements:null}));
  finish(base);
  await new Promise(resolve=>setImmediate(resolve));
  cache.setDesired([0]);
  assert.equal(signal.aborted,false);
  assert.equal(requests,1);
  assert.equal(cache.get(0),base);
  assert.equal(data.read(cell,cache.get(0)).forest,.15);
  assert.equal(data.read(cell,cache.get(0)).exact,true);
});

test("forest and ownership cannot change cached contour/coast geometry",()=>{
  const sample=cell=>({elevation:100+cell.x+cell.y,forest:cell.x/64,moisture:.5,lakeDepth:0,biome:4,claims:new Map([[0,1]])});
  const args={face:"PositiveZ",tx:0,ty:0,size:64,step:4,sample,cityCount:1,seaLevel:110};
  const changed={...args,sample:cell=>({...sample(cell),forest:0,claims:new Map()})};
  const staticKinds=["contour","coast"],dynamicKinds=["forest","boundary"];
  const original=buildWorldTile({...args,kinds:staticKinds});
  assert.ok(original.length>0);
  assert.deepEqual(original,buildWorldTile({...changed,kinds:staticKinds}));
  assert.notDeepEqual(buildWorldTile({...args,kinds:dynamicKinds}),buildWorldTile({...changed,kinds:dynamicKinds}));
});

test("halo dependencies cover interpolation on all cube seams and corners",()=>{
  const data=new SphereMapData({...options,faceSize:416,faces:FACE_NAMES});
  for(const face of FACE_NAMES)for(const tx of [0,6,12])for(const ty of [0,6,12]){
    const tile={face,tx,ty},keys=data.dependenciesFor(tile),predicted=new Set(keys);
    assert.equal(data.dependenciesFor(tile),keys);
    const actual=new Set();
    const sample=createSurfaceSampler({size:416,stride:1,origin:0,read:(f,x,y)=>{
      const l=locateFace(facePoint(f,x,y,416));
      actual.add(data.tile({face:l.face,x:Math.max(0,Math.min(415,Math.floor((l.u+1)*208))),y:Math.max(0,Math.min(415,Math.floor((l.v+1)*208)))}));
      return base;
    }});
    for(let x=-2.5;x<=33.5;x++)for(let y=-2.5;y<=33.5;y++)sample(locateFace(facePoint(face,tx*32+x,ty*32+y,416)));
    for(const key of actual)assert.ok(predicted.has(key),`${face}:${tx}:${ty} missed ${key}`);
  }
});
