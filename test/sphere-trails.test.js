import test from "node:test";
import assert from "node:assert/strict";
import {TrailGeometryCache,trailKey} from "../visualizer/sphere-trails.js";
import {facePoint} from "../visualizer/sphere-cartography.js";
const c=(x,y,face="PositiveZ")=>({face,x,y});
const e=(from,to,strength=.5)=>({from,to,strength});
test("bent trails round in world space; changing traffic does not rebuild their geometry",()=>{
  const edges=[e(c(10,10),c(11,10)),e(c(11,10),c(11,11))],original=structuredClone(edges);
  const cache=new TrailGeometryCache(),paths=cache.get(edges,64);
  assert.equal(paths.length,2);assert.ok(paths.every(p=>p.points.length>2));
  assert.deepEqual(paths[0].points.at(-1),paths[1].points[0]);
  assert.deepEqual(paths[0].points[0],facePoint("PositiveZ",10,10,64));
  assert.deepEqual(paths[1].points.at(-1),facePoint("PositiveZ",11,11,64));
  assert.notDeepEqual(paths[0].points.at(-1),facePoint("PositiveZ",11,10,64));
  assert.equal(cache.get(edges.map(edge=>({...edge,strength:.2})),64),paths);
  assert.equal(cache.builds,1);assert.deepEqual(edges,original);
});
test("junctions remain connected and vanished edges leave no ghost roads",()=>{
  const center=c(10,10),edges=[e(c(9,10),center),e(center,c(11,10)),e(center,c(10,11))];
  const cache=new TrailGeometryCache(),paths=cache.get(edges,64),point=facePoint("PositiveZ",10,10,64);
  assert.equal(paths.length,3);
  for(const path of paths)assert.ok(path.points.some(p=>JSON.stringify(p)===JSON.stringify(point)));
  const reduced=cache.get(edges.slice(1),64);
  assert.equal(reduced.length,2);assert.ok(reduced.every(p=>p.key!==trailKey(edges[0])));
  assert.deepEqual(cache.get([],64),[]);
});
test("loops and cube-seam paths remain continuous and on the unit sphere",()=>{
  const vertices=[c(10,10),c(11,10),c(11,11),c(10,11)];
  const loop=vertices.map((from,i)=>e(from,vertices[(i+1)%4]));
  for(const edges of [loop,[e(c(63,10),c(0,10,"PositiveX")),e(c(0,10,"PositiveX"),c(0,11,"PositiveX"))]]){
    const paths=new TrailGeometryCache().get(edges,64);
    assert.equal(paths.length,edges.length);
    for(const path of paths)for(const point of path.points)assert.ok(Math.abs(Math.hypot(point.x,point.y,point.z)-1)<1e-12);
    for(let i=0;i<paths.length-1;i++)assert.deepEqual(paths[i].points.at(-1),paths[i+1].points[0]);
    if(edges===loop)assert.deepEqual(paths.at(-1).points.at(-1),paths[0].points[0]);
  }
});
