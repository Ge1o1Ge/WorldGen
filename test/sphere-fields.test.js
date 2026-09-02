import test from "node:test";
import assert from "node:assert/strict";
import {buildFieldGroups,FieldGeometryCache,groupedField} from "../visualizer/sphere-fields.js";
import {facePoint,locateFace} from "../visualizer/sphere-cartography.js";
const plot=(x,y,id=`${x}:${y}`,face="PositiveZ")=>({id,face,x,y,buildingTypeId:"garden",status:"active"});
const city=buildings=>({id:"village",buildings});
const unproject=p=>{const f=locateFace(p);return {x:(f.u+1)*16-.5,y:(f.v+1)*16-.5};};
test("adjacent plots form one symbol and one rounded outer boundary without cell dividers",()=>{
  const groups=buildFieldGroups(city([plot(5,5),plot(6,5),plot(5,6)]),32);
  assert.equal(groups.length,1);assert.equal(groups[0].members.length,3);assert.equal(groups[0].rings.length,1);
  const points=groups[0].rings[0].map(unproject);
  assert.ok(points.length>8);assert.ok(points.every(p=>p.x>=4.5-1e-8&&p.x<=6.5+1e-8));
  assert.ok(!points.some(p=>Math.abs(p.x-5.5)<.001&&p.y>4.6&&p.y<5.4));
});
test("diagonally touching fields stay separate and a courtyard hole is retained",()=>{
  assert.equal(buildFieldGroups(city([plot(5,5),plot(6,6)]),32).length,2);
  const plots=[];for(let x=4;x<=6;x++)for(let y=4;y<=6;y++)if(x!==5||y!==5)plots.push(plot(x,y));
  const groups=buildFieldGroups(city(plots),32);
  assert.equal(groups.length,1);assert.equal(groups[0].rings.length,2);
  const anchor=unproject(groups[0].anchor);assert.ok(Math.abs(anchor.x-5)>.1||Math.abs(anchor.y-5)>.1);
});
test("corners touching a house stay exact while free corners are rounded",()=>{
  const field=plot(5,5),house={...plot(6,5,"house"),buildingTypeId:"house"};
  const ring=buildFieldGroups(city([field,house]),32)[0].rings[0];
  for(const y of [4.5,5.5])assert.ok(ring.some(p=>Math.hypot(p.x-facePoint("PositiveZ",5.5,y,32).x,p.y-facePoint("PositiveZ",5.5,y,32).y,p.z-facePoint("PositiveZ",5.5,y,32).z)<1e-9));
  assert.ok(!ring.some(p=>Math.hypot(p.x-facePoint("PositiveZ",4.5,4.5,32).x,p.y-facePoint("PositiveZ",4.5,4.5,32).y,p.z-facePoint("PositiveZ",4.5,4.5,32).z)<1e-9));
});
test("a field spanning a cube seam has no artificial face border",()=>{
  const groups=buildFieldGroups(city([plot(31,12,"a"),plot(0,12,"b","PositiveX")]),32);
  assert.equal(groups.length,1);assert.equal(groups[0].members.length,2);assert.equal(groups[0].rings.length,1);
});
test("world geometry ignores camera and resident/progress updates but rebuilds on land-use changes",()=>{
  const cache=new FieldGeometryCache(),settlements=[city([plot(5,5),plot(6,5)])];
  const a=cache.get(settlements,32);settlements[0].buildings[0].residents=25;
  assert.equal(cache.get(structuredClone(settlements),32),a);assert.equal(cache.builds,1);
  settlements[0].buildings[1].status="building";
  assert.equal(groupedField(settlements[0].buildings[1]),false);
  assert.notEqual(cache.get(settlements,32),a);assert.equal(cache.builds,2);
});
test("orchards and annual fields form separate groups even when adjacent",()=>{
  const settlements=[city([plot(5,5,"annual"),plot(6,5,"trees")])],plots=[{id:"annual",landUse:"field"},{id:"trees",landUse:"orchard"}];
  const cache=new FieldGeometryCache(),groups=cache.get(settlements,32,plots);
  assert.equal(groups.length,2);assert.deepEqual(groups.map(group=>group.landUse).sort(),["field","orchard"]);
  assert.ok(groups.every(group=>group.members.length===1));
});
