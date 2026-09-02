import test from 'node:test';
import assert from 'node:assert/strict';
import {symbolAtlasSvg,symbolInstances} from '../visualizer/sphere-webgl-symbols.js';

test('GPU symbol atlas keeps editable SVG paths and deterministic tiles',()=>{
  const atlas={palette:{ink:'#123456',water:'#397e9e'},symbols:[
    {id:'one',role:'ink',path:'M-8 0H8',fill:false},
    {id:'two',role:'water',path:'M0-8V8',fill:false}
  ]};
  const result=symbolAtlasSvg(atlas,{tile:48,columns:1});
  assert.equal(result.width,48);assert.equal(result.height,96);
  assert.match(result.svg,/M-8 0H8/);assert.match(result.svg,/#397e9e/);
  assert.match(result.svg,/translate\(24 72\)/);
});

test('GPU symbols retain world anchors and pack screen size only as an attribute',()=>{
  const uv=new Map([['tree',[0,.5,.25,1]]]);
  const packed=symbolInstances([{id:'tree',point:{x:.2,y:.3,z:.9},size:19,opacity:.7}],uv,2);
  assert.equal(packed.length,10);
  assert.deepEqual([...packed.slice(0,3)].map(value=>Number(value.toFixed(4))),[.2,.3,.9]);
  assert.equal(packed[7],38);assert.ok(Math.abs(packed[8]-.7)<1e-6);
});
