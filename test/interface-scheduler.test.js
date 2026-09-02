import test from 'node:test';
import assert from 'node:assert/strict';
import {interfaceWorkCanWait,interfaceWorkDelay,MAX_INTERFACE_LATENCY_MS} from '../visualizer/interface-scheduler.js';

test('continuous map work cannot starve interface work past its deadline',()=>{
  assert.equal(interfaceWorkCanWait({queuedAt:100,now:100+MAX_INTERFACE_LATENCY_MS-1,softBlocked:true}),true);
  assert.equal(interfaceWorkCanWait({queuedAt:100,now:100+MAX_INTERFACE_LATENCY_MS,softBlocked:true}),false);
});

test('direct camera input remains a hard blocker',()=>{
  assert.equal(interfaceWorkCanWait({queuedAt:0,now:5000,hardBlocked:true,softBlocked:true}),true);
});

test('retry delay is bounded by the original deadline',()=>{
  assert.equal(interfaceWorkDelay({queuedAt:100,now:200,requested:120}),120);
  assert.equal(interfaceWorkDelay({queuedAt:100,now:520,requested:120}),30);
  assert.equal(interfaceWorkDelay({queuedAt:100,now:600,requested:120}),0);
});
