import test from "node:test";
import assert from "node:assert/strict";
import {FACE_NAMES,facePoint,locateFace,blend} from "../visualizer/sphere-cartography.js";
import {createLakeSurfaceSampler} from "../visualizer/sphere-water.js";
import {landMaskAlpha,worldTileSymbols} from "../visualizer/sphere-world-geometry.js";

const location=(x,y,size=416,face="NegativeZ")=>locateFace(facePoint(face,x,y,size));

test("Lugovaya Stoyanka remains dry: deep coarse neighbours cannot flood its homes",()=>{
  // Regression from the live seed 271828: (220,220) was drawn at 5.40m depth,
  // although the canonical hydrology sample (55,55) contains no lake.
  const values=new Map([["54:54",7.9903717],["55:54",9.474442],["54:55",8.7790985]]);
  const sample=createLakeSurfaceSampler({faceSize:416,resolution:104,readDepth:(_face,x,y)=>values.get(`${x}:${y}`)??0});
  assert.ok(blend([7.9903717,9.474442,0,8.7790985],.625,.625)>5);
  for(const [x,y] of [[220,220],[220,221],[221,220],[220,223]]) {
    assert.ok(sample(location(x,y))<1e-9);
    for(const dx of [-.24,0,.24])for(const dy of [-.24,0,.24])
      assert.equal(landMaskAlpha(100,sample(location(x+dx,y+dy)),62),255);
  }
  assert.ok(sample(location(218,218))>1);
});

test("every microcell center agrees with its containing coarse cell on all six faces",()=>{
  const depth=(face,x,y)=>((FACE_NAMES.indexOf(face)+x*3+y*7)%5===0?40:0);
  const sample=createLakeSurfaceSampler({faceSize:32,resolution:8,readDepth:depth});
  for(const face of FACE_NAMES)for(let y=0;y<32;y++)for(let x=0;x<32;x++) {
    const expected=depth(face,Math.floor((x+.5)/4),Math.floor((y+.5)/4))>1;
    assert.equal(sample(location(x,y,32,face))>1,expected,`${face}:${x}:${y}`);
  }
});

test("changing lake depth changes colour depth but never the shoreline",()=>{
  const sampler=depth=>createLakeSurfaceSampler({faceSize:32,resolution:8,readDepth:(_face,x)=>x<4?depth:0});
  const shallow=sampler(2),deep=sampler(9000);
  for(let x=14;x<17;x+=.025) {
    const p=location(x,10,32);
    assert.equal(shallow(p)>1,deep(p)>1);
  }
  assert.ok(shallow(location(15,10,32))>1);
  assert.equal(deep(location(16,10,32)),0);
  assert.equal(shallow(location(15.5,10,32)),1);
});

test("water classification stays continuous across cube seams",()=>{
  const sample=createLakeSurfaceSampler({faceSize:32,resolution:8,readDepth:face=>face==="PositiveZ"?20:0});
  for(const z of [-.7,0,.7]) {
    const left=locateFace({x:1-1e-8,y:z,z:1});
    const right=locateFace({x:1+1e-8,y:z,z:1});
    assert.ok(Math.abs(sample(left)-sample(right))<.00001);
  }
});

test("land symbols and river mask use the same corrected lake surface",()=>{
  const lake=createLakeSurfaceSampler({faceSize:64,resolution:16,readDepth:(_face,x)=>x<8?30:0});
  const symbols=worldTileSymbols({face:"PositiveZ",tx:0,ty:0,size:64,tileSize:64,zoom:8,seed:42,settlements:[],seaLevel:62,
    sample:p=>({elevation:100,forest:.8,biome:4,lakeDepth:lake(location(p.x,p.y,64,p.face))})});
  assert.ok(symbols.length>0);
  assert.ok(symbols.every(p=>landMaskAlpha(100,lake(location(p.x,p.y,64,p.face)),62)===255));
});

test("exact shoreline follows a sloping lake bed, not a binary-cell midpoint",()=>{
  const sample=createLakeSurfaceSampler({faceSize:32,resolution:32,readDepth:()=>0,
    readShore:(_face,x)=>12.4-x});
  for(const x of [10,11,12,12.2,12.4,12.6,13,14]) {
    const s=sample.surface(location(x,10,32));
    assert.ok(Math.abs(s.shore-(12.4-x))<1e-10);
    assert.equal(s.depth>1,x<12.4);
  }
});

test("exact data preserves classification on all fine-cell centres, including shallow banks",()=>{
  const field=(face,x,y)=>(FACE_NAMES.indexOf(face)+x*3+y*7)%11-5;
  const sample=createLakeSurfaceSampler({faceSize:32,resolution:32,readDepth:()=>0,readShore:field});
  for(const face of FACE_NAMES)for(let y=0;y<32;y++)for(let x=0;x<32;x++) {
    const s=sample.surface(location(x,y,32,face));
    assert.ok(Math.abs(s.shore-field(face,x,y))<1e-9);
    assert.equal(s.depth>1,field(face,x,y)>0);
  }
});

test("exact shoreline field is continuous across cube seams",()=>{
  const sample=createLakeSurfaceSampler({faceSize:32,resolution:32,readDepth:()=>0,
    readShore:face=>face==="PositiveZ"?5:-7});
  for(const z of [-.7,0,.7]) {
    const a=locateFace({x:1-1e-8,y:z,z:1}),b=locateFace({x:1+1e-8,y:z,z:1});
    assert.ok(Math.abs(sample.surface(a).shore-sample.surface(b).shore)<.00001);
  }
});
