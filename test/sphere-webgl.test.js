import test from 'node:test';
import assert from 'node:assert/strict';
import {chunkTexturePixels,previewTexturePixels,rankSnapshots,snapshotUv,upscaleTexturePixels,viewMatrixForGl,worldMatrixForGl} from '../visualizer/sphere-webgl.js';

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

test('static frame snapshot reprojects world points through its captured camera',()=>{
  const geometry={width:800,height:600,centerX:400,centerY:294,radius:243};
  const identity=[1,0,0,0,1,0,0,0,1];
  assert.deepEqual(snapshotUv({x:0,y:0,z:1},identity,geometry),{x:.5,y:.51,z:1});
  assert.equal(snapshotUv({x:0,y:0,z:-1},identity,geometry),null,'old far hemisphere is never stretched into view');
  const right=snapshotUv({x:1,y:0,z:1},identity,geometry);
  assert.ok(right.x>.5&&right.y===.51);
});

test('snapshot pool prefers the nearest camera and then the nearest scale',()=>{
  const matrix=[1,0,0,0,1,0,0,0,1];
  const slots=[
    {id:'side',forward:[.8,0,.6],radius:400,stamp:3},
    {id:'wrong-scale',forward:[0,0,1],radius:80,stamp:4},
    {id:'nearest',forward:[0,0,1],radius:390,stamp:2}
  ];
  assert.deepEqual(rankSnapshots(slots,matrix,400).map(item=>item.id),['nearest','wrong-scale','side']);
  assert.deepEqual(slots.map(item=>item.id),['side','wrong-scale','nearest'],'ranking does not mutate the retained pool');
});

test('overview texture expands to exact face dimensions without inventing samples',()=>{
  const source=new Uint8Array([1,2,3,255,10,20,30,255,40,50,60,255,70,80,90,255]);
  const result=upscaleTexturePixels(source,2,4);
  assert.equal(result.length,4*4*4);
  assert.deepEqual([...result.slice(0,4)],[1,2,3,255]);
  assert.deepEqual([...result.slice(-4)],[70,80,90,255]);
  assert.ok(result[4]>source[0]&&result[4]<source[4],'interior texels are bilinearly interpolated');
});

test('exact terrain chunk uses the same cartographic palette as the overview',()=>{
  const chunk={width:2,elevationMeters:[0,80,120,50],forestCover:[0,.8,.5,.2],biome:[0,4,3,5]};
  assert.deepEqual([...chunkTexturePixels(chunk,62)],[...previewTexturePixels({resolution:2,elevation:chunk.elevationMeters,forest:chunk.forestCover,biome:chunk.biome},62)]);
});
