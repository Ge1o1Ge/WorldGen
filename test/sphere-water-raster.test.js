import test from "node:test";
import assert from "node:assert/strict";
import {paintWaterRaster} from "../visualizer/sphere-water-raster.js";

function raster({width=21,height=11,sea=()=>-100,lake=(x)=>x-10,...options}={}) {
  const pixels=new Uint8ClampedArray(width*height*4).fill(255),mask=new Uint8ClampedArray(width*height*4);
  const ocean=new Float32Array(width*height),lakes=new Float32Array(width*height);
  const waterColors=new Uint8ClampedArray(width*height*3); // Black water / white land makes alpha observable.
  for(let y=0;y<height;y++)for(let x=0;x<width;x++){
    ocean[y*width+x]=sea(x,y);lakes[y*width+x]=lake(x,y);
  }
  paintWaterRaster({pixels,mask,ocean,lakes,waterColors,width,height,...options});
  return {pixels,mask,red:(x,y=5)=>pixels[(y*width+x)*4],land:(x,y=5)=>mask[(y*width+x)*4+3]};
}

test("water fill and land-ink mask share exactly the same antialiased edge",()=>{
  const image=raster({stroke:false});
  for(let x=0;x<21;x++)assert.equal(image.red(x),image.land(x));
  assert.equal(image.land(9),255);assert.equal(image.land(10),128);assert.equal(image.land(11),0);
});

test("shoreline stroke is centred on that edge, independent of physical depth",()=>{
  for(const slope of [.01,1,2000]) {
    const image=raster({lake:x=>(x-10)*slope,fill:false,shoreColor:[0,0,0]});
    assert.equal(image.red(10),51);
    assert.equal(image.red(9),image.red(11));
    assert.equal(image.red(8),255);assert.equal(image.red(12),255);
  }
});

test("union of sea and lake does not draw a phantom coast inside continuous water",()=>{
  const image=raster({sea:x=>x-5,lake:x=>15-x,shoreColor:[255,0,0]});
  for(let x=0;x<21;x++)assert.equal(image.land(x),0);
  assert.equal(image.red(5),0);assert.equal(image.red(15),0);
});

test("islands and small lakes are handled without polygon closure or chunk seams",()=>{
  const image=raster({width:65,height:21,stroke:false,lake:(x,y)=>4-Math.hypot(x-32,y-10)});
  assert.equal(image.land(32,10),0);assert.equal(image.land(28,10),128);assert.equal(image.land(27,10),255);
  assert.equal(image.land(31,10),image.land(33,10));
  const island=raster({width:65,height:21,stroke:false,lake:(x,y)=>Math.hypot(x-32,y-10)-4});
  assert.equal(island.land(32,10),255);assert.equal(island.land(27,10),0);
});

test("CSS outline width follows pixel density without moving the world edge",()=>{
  const one=raster({fill:false,shoreColor:[0,0,0]});
  const two=raster({width:41,pixelRatio:2,lake:x=>x-20,fill:false,shoreColor:[0,0,0]});
  assert.equal(one.red(10),two.red(20));assert.equal(one.land(10),two.land(20));
  assert.ok(two.red(19)<one.red(9));
  assert.equal(two.red(18),255);
});

test("drag sampling respects the same edge and fills complete blocks",()=>{
  const image=raster({width:24,height:12,step:2,stroke:false});
  assert.equal(image.land(10,4),128);
  for(let y=0;y<12;y+=2)for(let x=0;x<24;x+=2){
    assert.equal(image.land(x,y),image.land(x+1,y+1));
    assert.equal(image.red(x,y),image.land(x,y));
  }
});

test("globe horizon and viewport limits do not produce a false shoreline",()=>{
  const image=raster({sea:x=>x<4?NaN:-100,lake:()=>1,shoreColor:[255,0,0]});
  assert.equal(image.land(3),0);assert.equal(image.red(3),255);
  assert.equal(image.red(4),0);assert.equal(image.red(20),0);
});

test("uniform dry and wet fields do not receive a shoreline tint",()=>{
  const dry=raster({lake:()=>-1}),wet=raster({lake:()=>1}),sea=raster({sea:()=>0,lake:()=>-1});
  assert.equal(dry.red(10),255);assert.equal(dry.land(10),255);
  assert.equal(wet.red(10),0);assert.equal(wet.land(10),0);
  assert.equal(sea.red(10),0);
});
