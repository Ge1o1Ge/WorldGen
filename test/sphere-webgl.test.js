import test from 'node:test';
import assert from 'node:assert/strict';
import {chunkTexturePixels,lakeTexturePixels,landTexturePixels,previewTexturePixels,upscaleElevation,upscaleTexturePixels,viewMatrixForGl,waterTexturePixels,worldMatrixForGl} from '../visualizer/sphere-webgl.js';
import {waterShoreByte} from '../visualizer/sphere-water.js';

test('GPU preview keeps water and land in one immutable cubed-sphere texture',()=>{
  const face={resolution:2,elevation:[0,80,120,50],forest:[0,.8,.5,.2],biome:[0,4,3,5]};
  const pixels=previewTexturePixels(face,62);
  assert.equal(pixels.length,16);
  assert.equal(pixels[3],255);
  assert.equal(pixels[7],255);
  assert.ok(pixels[2]>pixels[0],'water remains blue');
  assert.ok(pixels[5]>pixels[4],'forest land remains green');
  assert.deepEqual([...face.elevation],[0,80,120,50]);
});

test('camera row matrix is transposed exactly once for GLSL column-major uniforms',()=>{
  assert.deepEqual([...worldMatrixForGl([0,1,2,3,4,5,6,7,8])],[0,3,6,1,4,7,2,5,8]);
  assert.deepEqual([...viewMatrixForGl([0,1,2,3,4,5,6,7,8])],[0,1,2,3,4,5,6,7,8]);
});

test('overview texture expands to exact face dimensions without inventing samples',()=>{
  const source=new Uint8Array([1,2,3,255,10,20,30,255,40,50,60,255,70,80,90,255]);
  const result=upscaleTexturePixels(source,2,4);
  assert.equal(result.length,4*4*4);
  assert.deepEqual([...result.slice(0,4)],[1,2,3,255]);
  assert.deepEqual([...result.slice(-4)],[70,80,90,255]);
  assert.ok(result[4]>source[0]&&result[4]<source[4],'interior texels are bilinearly interpolated');
});

test('elevation expands as float data and preserves exact corner heights',()=>{
  const result=upscaleElevation([0,100,200,300],2,4);
  assert.ok(result instanceof Float32Array);assert.equal(result.length,16);
  assert.equal(result[0],0);assert.equal(result.at(-1),300);
  assert.ok(result[1]>0&&result[1]<100);
});

test('exact terrain chunk uses the same cartographic palette as the overview',()=>{
  const chunk={width:2,elevationMeters:[0,80,120,50],forestCover:[0,.8,.5,.2],biome:[0,4,3,5]};
  assert.deepEqual([...chunkTexturePixels(chunk,62)],[...previewTexturePixels({resolution:2,elevation:chunk.elevationMeters,forest:chunk.forestCover,biome:chunk.biome},62)]);
});

test('GPU material keeps smooth land colour separate from a sharp water mask and depth',()=>{
  const face={resolution:2,elevation:[0,80,120,50],forest:[0,.8,.5,.2],biome:[0,4,3,5]};
  const land=landTexturePixels(face),water=waterTexturePixels(face,62);
  assert.equal(land.length,16);assert.equal(water.length,8);
  assert.ok(water[0]>128);assert.ok(water[1]>0);assert.ok(water[2]<128);
  assert.ok(water[6]>128);assert.ok(land[2]<226,'the ocean texel retains underlying land material instead of pre-blending the shoreline');
});

test('lake material preserves a signed sub-cell shoreline and independent depth',()=>{
  const pixels=lakeTexturePixels(2,(x,y)=>({shore:x===0?-2:2,depth:y===0?3:60}));
  assert.ok(pixels[0]<128&&pixels[2]>128);assert.ok(pixels[1]<pixels[5]);
});

test('shore quantisation never turns a shallow bank into an unsigned zero texel',()=>{
  assert.equal(waterShoreByte(-.01),127);
  assert.equal(waterShoreByte(0),127);
  assert.equal(waterShoreByte(.01),129);
});
