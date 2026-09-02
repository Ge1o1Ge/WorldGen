import test from 'node:test';
import assert from 'node:assert/strict';
import {parseRenderPacket,RenderPacket} from '../visualizer/sphere-render-packet.js';

function packet(payloadLength=8){
  const descriptorBytes=32,offset=16+descriptorBytes,buffer=new ArrayBuffer(offset+payloadLength),bytes=new Uint8Array(buffer),view=new DataView(buffer);
  bytes.set([87,71,82,80]);view.setUint16(4,2,true);view.setUint16(6,descriptorBytes,true);view.setUint32(8,1,true);view.setUint32(12,buffer.byteLength,true);
  bytes[16]=RenderPacket.opcode.texture2DArray;bytes[17]=RenderPacket.resource.water;bytes[18]=RenderPacket.format.rg8;
  view.setUint16(20,2,true);view.setUint16(22,2,true);view.setUint16(24,1,true);view.setUint32(28,offset,true);view.setUint32(32,payloadLength,true);view.setUint32(36,17,true);
  for(let i=0;i<payloadLength;i++)bytes[offset+i]=i+1;
  return buffer;
}

test('render packet exposes GPU-ready byte ranges without JSON decoding',()=>{
  const parsed=parseRenderPacket(packet());
  assert.equal(parsed.version,2);assert.equal(parsed.commands.length,1);
  assert.deepEqual({...parsed.commands[0],payload:[...parsed.commands[0].payload]},{opcode:1,resource:2,format:2,flags:0,width:2,height:2,layers:1,mipLevel:0,offset:48,length:8,revision:17,payload:[1,2,3,4,5,6,7,8],x:0,y:0,layer:0});
});

test('render packet exposes texture-array patch coordinates',()=>{
  const buffer=packet(),bytes=new Uint8Array(buffer),view=new DataView(buffer);
  bytes[16]=RenderPacket.opcode.texture2DArrayPatch;view.setUint16(40,9,true);view.setUint16(42,11,true);view.setUint16(44,4,true);
  const command=parseRenderPacket(buffer).commands[0];assert.deepEqual([command.x,command.y,command.layer],[9,11,4]);
});

test('elevation has a dedicated float texture command contract',()=>{
  assert.equal(RenderPacket.resource.elevation,3);assert.equal(RenderPacket.format.r32f,3);
});

test('render packet rejects payload ranges outside its envelope',()=>{
  const buffer=packet(),view=new DataView(buffer);view.setUint32(32,999,true);
  assert.throws(()=>parseRenderPacket(buffer),/outside/);
});
