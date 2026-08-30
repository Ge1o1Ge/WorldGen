import test from "node:test";
import assert from "node:assert/strict";
import {MobileUnitLayer,interpolateUnitPath,scoutMovementPath} from "../visualizer/sphere-mobile-units.js";

const cell=n=>({face:"PositiveX",x:n,y:4});

test("scout movement follows every newly reported outbound cell",()=>{
  const previous={id:"s",phase:"outbound",routeIndex:1,path:[cell(0),cell(1)],face:"PositiveX",x:1,y:4};
  const next={id:"s",phase:"outbound",routeIndex:4,path:[cell(0),cell(1),cell(2),cell(3),cell(4)],face:"PositiveX",x:4,y:4};
  assert.deepEqual(scoutMovementPath(previous,next).map(p=>p.x),[1,2,3,4]);
});

test("one coarse server packet retains the outward turn and complete return",()=>{
  const previous={id:"s",phase:"outbound",routeIndex:1,path:[cell(0),cell(1)],face:"PositiveX",x:1,y:4};
  const next={id:"s",phase:"returned",routeIndex:0,path:[cell(0),cell(1),cell(2),cell(3)],face:"PositiveX",x:0,y:4};
  assert.deepEqual(scoutMovementPath(previous,next).map(p=>p.x),[1,2,3,2,1,0]);
});

test("a complete expedition first seen in one 30-day packet still animates out and home",()=>{
  const home=cell(0),a=cell(1),turn=cell(2);
  const path=scoutMovementPath(null,{id:"s",phase:"returned",routeIndex:0,path:[home,a,turn],face:home.face,x:home.x,y:home.y});
  assert.deepEqual(path,[home,a,turn,a,home]);
});

test("mobile interpolation remains on the sphere instead of cutting through it",()=>{
  const point=interpolateUnitPath([{x:1,y:0,z:0},{x:0,y:1,z:0}],.5);
  assert.ok(Math.abs(Math.hypot(point.x,point.y,point.z)-1)<1e-12);
  assert.ok(point.x>0&&point.y>0);
});

function layerHarness(){
  let now=0,scheduled=0;
  const context=new Proxy({},{get:(target,key)=>target[key]??(()=>{})});
  const canvas={style:{},width:100,height:100,getContext:()=>context};
  const layer=new MobileUnitLayer(canvas,{
    pointForCell:value=>({x:1,y:value.x/100,z:value.y/100}),
    project:()=>({x:20,y:20,z:1}),now:()=>now,
    request:()=>++scheduled,cancel:()=>{},duration:100
  });
  return {layer,setNow:value=>{now=value;},scheduled:()=>scheduled};
}

test("an identical packet does not restart an animation already in progress",()=>{
  const {layer,setNow}=layerHarness();
  const scout={id:"s",departureDay:10,phase:"outbound",routeIndex:1,path:[cell(0),cell(1)],face:"PositiveX",x:1,y:4};
  layer.update([scout],11,{animate:true});const started=layer.units.get("s").started;
  setNow(40);layer.update([{...scout,food:.2}],11,{animate:true});
  assert.equal(layer.units.get("s").started,started);
  assert.equal(layer.units.get("s").scout.food,.2);
});

test("a completed returned expedition is not replayed by later server packets",()=>{
  const {layer,setNow}=layerHarness();const home=cell(0),turn=cell(2);
  const returned={id:"s",departureDay:10,phase:"returned",routeIndex:0,path:[home,cell(1),turn],face:home.face,x:home.x,y:home.y};
  layer.update([returned],20,{animate:true});assert.equal(layer.units.size,1);
  setNow(101);layer.render();assert.equal(layer.units.size,0);assert.equal(layer.completed.size,1);
  layer.update([returned],21,{animate:true});assert.equal(layer.units.size,0);assert.equal(layer.completed.size,1);
});
