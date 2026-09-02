import test from "node:test";
import assert from "node:assert/strict";
import {mergeLiveSimulationState,SimulationLiveChannel} from "../visualizer/simulation-live.js";

class FakeSocket {
  constructor(){this.readyState=0;this.listeners=new Map();this.sent=[];}
  addEventListener(type,listener){this.listeners.set(type,listener);}
  emit(type,value={}){this.listeners.get(type)?.(value);}
  send(value){this.sent.push(JSON.parse(value));}
  close(){this.readyState=3;this.emit("close");}
}

test("compact live state preserves heavy settlement details and merges changing homes",()=>{
  const current={revision:1,events:[{id:"a",day:1}],rivers:[{id:1}],riverRevision:3,weatherMap:{revision:1,temperature:[10],climate:{months:12},local:{indices:[3]}},cities:[{id:"camp",name:"Camp",stocks:{food:2},industries:[{id:"mill"}],biology:{knownAnimals:["rabbit"]},exploration:{knownCells:4},settlement:{decision:"wait",tasks:[]},homes:[{id:"h1",kind:"house",status:"active",residents:4}]}]};
  const patch={revision:2,map:{revision:2,claimsRevision:2},riverRevision:3,weatherMap:{revision:2,temperature:[11]},events:[{id:"b",day:2}],cities:[{id:"camp",stocks:{food:1},biology:{cropHistory:{barley:{seasons:1}}},exploration:{knownCells:9},settlement:{laborUsedHours:7},homes:[{id:"h1",status:"abandoned",residents:0}]}]};
  const merged=mergeLiveSimulationState(current,patch),city=merged.cities[0];
  assert.equal(city.name,"Camp");assert.deepEqual(city.industries,[{id:"mill"}]);assert.equal(city.settlement.decision,"wait");
  assert.equal(city.settlement.laborUsedHours,7);assert.equal(city.homes[0].kind,"house");assert.equal(city.homes[0].status,"abandoned");
  assert.deepEqual(city.biology.knownAnimals,["rabbit"]);assert.equal(city.biology.cropHistory.barley.seasons,1);assert.equal(city.exploration.knownCells,9);
  assert.deepEqual(merged.weatherMap,{revision:2,temperature:[11],climate:{months:12},local:{indices:[3]}});
  assert.equal(merged._liveMapChanged,true);assert.equal(merged.map.claimsRevision,2);
  assert.deepEqual(merged.rivers,[{id:1}]);assert.equal(merged._riversChanged,false);
  assert.deepEqual(merged.events.map(event=>event.id),["b","a"]);
});

test("live river revisions replace the retained calculated network only when sent",()=>{
  const merged=mergeLiveSimulationState({revision:1,cities:[],events:[],rivers:[{id:1}],riverRevision:2},
    {revision:2,cities:[],events:[],rivers:[{id:4}],riverRevision:3});
  assert.deepEqual(merged.rivers,[{id:4}]);assert.equal(merged.riverRevision,3);assert.equal(merged._riversChanged,true);
});

test("live channel sends only run, speed changes and pause over one socket",()=>{
  const socket=new FakeSocket(),statuses=[];
  const channel=new SimulationLiveChannel({url:"ws://world/live",socketFactory:()=>socket,onStatus:value=>statuses.push(value)});
  channel.connect();socket.readyState=1;socket.emit("open");channel.setSpeed(30);channel.start();channel.setSpeed(7);channel.pause();
  assert.deepEqual(socket.sent,[{type:"run",speed:30},{type:"run",speed:7},{type:"pause"}]);
  assert.equal(channel.playing,false);assert.ok(statuses.includes("ready"));
});

test("live channel routes binary render packets without JSON parsing",()=>{
  const socket=new FakeSocket(),received=[];
  const channel=new SimulationLiveChannel({url:"ws://world/live",socketFactory:()=>socket,onBinary:(data,bytes)=>received.push([data,bytes])});
  channel.connect();const packet=new ArrayBuffer(12);socket.emit("message",{data:packet});
  assert.equal(socket.binaryType,"arraybuffer");assert.deepEqual(received,[[packet,12]]);
});

test("acknowledgement identifies the exact applied state patch",()=>{
  const socket=new FakeSocket();
  const channel=new SimulationLiveChannel({url:"ws://world/live",socketFactory:()=>socket});
  channel.connect();socket.readyState=1;socket.emit("open");channel.acknowledge(42);
  assert.deepEqual(socket.sent,[{type:"ack",sequence:42}]);
});
