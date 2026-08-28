import test from "node:test";
import assert from "node:assert/strict";
import {SimulationPlayback} from "../visualizer/simulation-playback.js";

const settle=()=>new Promise(resolve=>setImmediate(resolve));
function harness(advance){
  const timers=new Map(),errors=[];let id=0;
  const playback=new SimulationPlayback({advance,onError:e=>errors.push(e),setTimer:cb=>{timers.set(++id,cb);return id;},clearTimer:id=>timers.delete(id)});
  return {playback,timers,errors,tick:async()=>{const [id,cb]=timers.entries().next().value;timers.delete(id);cb();await settle();}};
}
test("start advances repeatedly until explicit pause; it is not a one-day button",async()=>{
  let days=0;const h=harness(async n=>{days+=n;});h.playback.start();await settle();
  for(let i=0;i<8;i++)await h.tick();
  assert.equal(days,9);assert.equal(h.timers.size,1);
  h.playback.pause();assert.equal(h.timers.size,0);assert.equal(h.playback.playing,false);
});
test("slow requests, repeated start and pause/resume never overlap or multiply timers",async()=>{
  let complete,calls=0;const h=harness(()=>{calls++;return new Promise(r=>{complete=r;});});
  h.playback.start();h.playback.start();await h.playback.step(30);
  h.playback.pause();h.playback.start();assert.equal(calls,1);
  complete();await settle();assert.equal(h.timers.size,1);await h.tick();assert.equal(calls,2);
  h.playback.pause();complete();await settle();assert.equal(h.timers.size,0);
});
test("network error stops without retrying an uncertain mutating request",async()=>{
  let calls=0;const h=harness(async()=>{calls++;throw new Error("offline");});
  h.playback.start();await settle();
  assert.equal(h.errors.length,1);assert.equal(h.playback.playing,false);assert.equal(h.playback.busy,false);
  assert.equal(h.timers.size,0);assert.equal(calls,1);
});
test("manual thirty-day step does not start autoplay",async()=>{
  const days=[];const h=harness(async n=>days.push(n));await h.playback.step(30);
  assert.deepEqual(days,[30]);assert.equal(h.timers.size,0);
});
test("native browser timers are not called with the playback controller as their receiver",async t=>{
  let scheduled,cancelled;
  const playback=new SimulationPlayback({advance:async()=>{}});
  t.mock.method(globalThis,"setTimeout",function(callback){assert.notEqual(this,playback);scheduled=callback;return 123;});
  t.mock.method(globalThis,"clearTimeout",function(id){assert.notEqual(this,playback);cancelled=id;});
  playback.start();await settle();assert.equal(typeof scheduled,"function");
  playback.pause();assert.equal(cancelled,123);
});
test("failure to schedule the next day visibly stops instead of leaving a false playing state",async()=>{
  const errors=[];const playback=new SimulationPlayback({advance:async()=>{},onError:e=>errors.push(e),setTimer:()=>{throw new Error("timer unavailable");}});
  playback.start();await settle();
  assert.equal(playback.playing,false);assert.equal(playback.busy,false);assert.equal(errors[0].message,"timer unavailable");
});
