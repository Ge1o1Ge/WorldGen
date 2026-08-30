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
  const current={revision:1,events:[{id:"a",day:1}],cities:[{id:"camp",name:"Camp",stocks:{food:2},industries:[{id:"mill"}],settlement:{decision:"wait",tasks:[]},homes:[{id:"h1",kind:"house",status:"active",residents:4}]}]};
  const patch={revision:2,map:{revision:2,claimsRevision:2},events:[{id:"b",day:2}],cities:[{id:"camp",stocks:{food:1},settlement:{laborUsedHours:7},homes:[{id:"h1",status:"abandoned",residents:0}]}]};
  const merged=mergeLiveSimulationState(current,patch),city=merged.cities[0];
  assert.equal(city.name,"Camp");assert.deepEqual(city.industries,[{id:"mill"}]);assert.equal(city.settlement.decision,"wait");
  assert.equal(city.settlement.laborUsedHours,7);assert.equal(city.homes[0].kind,"house");assert.equal(city.homes[0].status,"abandoned");
  assert.equal(merged._liveMapChanged,true);assert.equal(merged.map.claimsRevision,2);
  assert.deepEqual(merged.events.map(event=>event.id),["b","a"]);
});

test("live channel sends only run, speed changes and pause over one socket",()=>{
  const socket=new FakeSocket(),statuses=[];
  const channel=new SimulationLiveChannel({url:"ws://world/live",socketFactory:()=>socket,onStatus:value=>statuses.push(value)});
  channel.connect();socket.readyState=1;socket.emit("open");channel.setSpeed(30);channel.start();channel.setSpeed(7);channel.pause();
  assert.deepEqual(socket.sent,[{type:"run",speed:30},{type:"run",speed:7},{type:"pause"}]);
  assert.equal(channel.playing,false);assert.ok(statuses.includes("ready"));
});
