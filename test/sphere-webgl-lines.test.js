import test from 'node:test';
import assert from 'node:assert/strict';
import {lineVertexData,parseLineColor,sphereLineFragmentSource} from '../visualizer/sphere-webgl-lines.js';
import {partitionRetainedEntries,retainedCommitKeys,retainedTransitionNeeded} from '../visualizer/sphere-retained-layer.js';
import {dynamicRiverLines,OVERVIEW_SYMBOL_ZOOM,RIVER_CROSSFADE,riverClassVisibleAtZoom,riverDisplayRun,riverLandRuns,spherePathVisible,splitSpherePath} from '../visualizer/sphere-map-layer.js';

test('retained spherical line mesh emits two triangles for each world segment',()=>{
  const data=lineVertexData([{points:[{x:1,y:0,z:0},{x:0,y:1,z:0},{x:0,y:0,z:1}],color:'#804020',width:1.25,alpha:.5}],2);
  assert.equal(data.length,2*6*17);
  assert.deepEqual([...data.slice(0,3)],[1,0,0]);
  assert.equal(data[8],2.5,'CSS line width is converted to framebuffer pixels once');
  const color=[...data.slice(9,13)],expected=[128/255,64/255,32/255,.5];
  color.forEach((value,index)=>assert.ok(Math.abs(value-expected[index])<1e-6));
});

test('dash distances remain continuous along a spherical path',()=>{
  const data=lineVertexData([{points:[{x:1,y:0,z:0},{x:0,y:1,z:0},{x:0,y:0,z:1}],color:'#000000',dash:[7,3]}]);
  const firstEnd=data[2*17+13],secondStart=data[6*17+13],period=data[14];
  assert.ok(Math.abs(firstEnd-Math.PI/2)<1e-6);assert.equal(secondStart,firstEnd);assert.equal(period,10);
});

test('parallel channel offset is converted from CSS to framebuffer pixels',()=>{
  const data=lineVertexData([{points:[{x:1,y:0,z:0},{x:0,y:1,z:0}],color:'#000',offset:-1.5}],2);
  assert.equal(data[16],-3);
});

test('calculated rivers use one, two and broad GL strokes by channel class',()=>{
  const runs=[[{x:1,y:0,z:0},{x:0,y:1,z:0}]];
  const small=dynamicRiverLines({channelClass:'small',dischargeM3PerDay:80},runs,16,'#2780aa');
  const medium=dynamicRiverLines({channelClass:'medium',dischargeM3PerDay:4000},runs,16,'#2780aa');
  const major=dynamicRiverLines({channelClass:'major',widthMeters:64},runs,16,'#2780aa');
  assert.equal(small.length,1);assert.equal(medium.length,2);assert.equal(major.length,1);
  assert.equal(medium[0].offset,-medium[1].offset);assert.ok(major[0].width>10);
});

test('small calculated rivers enter the retained map at 3x',()=>{
  assert.equal(riverClassVisibleAtZoom('small',2.99),false);
  assert.equal(riverClassVisibleAtZoom('small',3),true);
  assert.equal(riverClassVisibleAtZoom('medium',1),true);
});

test('overview symbols remain visible at the 3x river scale',()=>{
  assert.ok(OVERVIEW_SYMBOL_ZOOM<=3);
  assert.equal(OVERVIEW_SYMBOL_ZOOM,1.8);
});

test('an incremental same-key update preserves the camera working set',()=>{
  assert.deepEqual(retainedCommitKeys(new Set(['old-tile','road-network']),['new-tile','road-network'],
    {incremental:true,replacesBase:true}),['old-tile','road-network','new-tile']);
  assert.deepEqual(retainedCommitKeys(new Set(['old-tile']),['new-tile'],
    {incremental:false,replacesBase:true}),['new-tile']);
});

test('only explicitly animated data revisions fade retained linework',()=>{
  assert.equal(retainedTransitionNeeded(1200,false),false);
  assert.equal(retainedTransitionNeeded(1200,true),true);
  assert.equal(retainedTransitionNeeded(0,true),false);
});

test('line fragment shader declares every transition uniform it reads',()=>{
  assert.match(sphereLineFragmentSource,/uniform\s+float\s+uOpacity\s*;/);
  assert.match(sphereLineFragmentSource,/coverage\s*\*\s*uOpacity/);
});

test('river transitions cannot include roads or parcel boundaries',()=>{
  const river={key:'river:1',animate:true},road={key:'trails',animate:false},parcel={key:'parcel-outlines',animate:false};
  assert.deepEqual(partitionRetainedEntries([road,river,parcel]),{fixed:[road,parcel],animated:[river]});
});

test('river revisions stay atomic until vertex interpolation replaces colour crossfade',()=>{
  assert.equal(RIVER_CROSSFADE,false);
});

test('display rounding preserves the exact server shoreline endpoint',()=>{
  const points=[[1,0,0],[.98,.2,0],[.94,.3,.15],[.9,.4,.17]];
  const run=riverDisplayRun(points);
  assert.deepEqual(run[0],{x:1,y:0,z:0});
  assert.deepEqual(run.at(-1),{x:.9,y:.4,z:.17});
});

test('line color accepts hex and rgb notation without browser APIs',()=>{
  assert.deepEqual(parseLineColor('#ff8000',.4),[1,128/255,0,.4]);
  assert.deepEqual(parseLineColor('rgb(12, 34, 56)'),[12/255,34/255,56/255,1]);
});

test('land and isobath runs share a refined spherical shoreline endpoint',()=>{
  const points=[{x:1,y:0,z:0},{x:0,y:1,z:0},{x:-1,y:0,z:0}];
  const wet=point=>point.x<.25;
  const land=splitSpherePath(points,wet,false,10),water=splitSpherePath(points,wet,true,10);
  assert.equal(land.length,1);assert.equal(water.length,1);
  const a=land[0].at(-1),b=water[0][0];
  assert.ok(Math.hypot(a.x-b.x,a.y-b.y,a.z-b.z)<.003);
  for(const point of [...land[0],...water[0]])assert.ok(Math.abs(Math.hypot(point.x,point.y,point.z)-1)<1e-12);
});

test('river smoothing cannot create dashed fragments inside a water body',()=>{
  const point=angle=>({x:Math.cos(angle),y:Math.sin(angle),z:0});
  const raw=[point(-.2),point(.2),point(-.2),point(-.4)];
  const water=point=>point.x>.995;
  assert.equal(raw.some(water),false,'the discrete river stays on land');
  const runs=riverLandRuns(raw,water);
  assert.equal(runs.length,3,'two hidden water crossings split the folded route');
  assert.ok(runs.every(run=>run.every(point=>!water(point))));
});

test('a narrow pond between two dry river vertices is still clipped',()=>{
  const point=angle=>({x:Math.cos(angle),y:Math.sin(angle),z:0});
  const raw=[point(-.12),point(.12)];
  const water=point=>Math.abs(Math.atan2(point.y,point.x))<.025;
  assert.equal(raw.some(water),false,'both retained route vertices are dry');
  const runs=riverLandRuns(raw,water);
  assert.equal(runs.length,2,'the hidden wet interval splits the line');
  assert.ok(runs.every(run=>run.every(point=>!water(point))));
  assert.ok(runs[0].at(-1).y<0&&runs[1][0].y>0);
});

test('river working set excludes paths outside the viewport',()=>{
  const project=point=>({x:point.x,y:point.y,z:1});
  assert.equal(spherePathVisible([{x:10,y:10,z:1},{x:90,y:90,z:1}],project,100,100),true);
  assert.equal(spherePathVisible([{x:180,y:10,z:1},{x:220,y:90,z:1}],project,100,100,20),false);
  assert.equal(spherePathVisible([{x:-30,y:10,z:1},{x:10,y:10,z:1}],project,100,100,20),true);
  assert.equal(spherePathVisible([{x:0,y:0,z:1}],()=>null,100,100),false);
});
