import test from 'node:test';
import assert from 'node:assert/strict';
import {calendarPosition,interpolateWorldDay} from '../visualizer/world-clock.js';

test('world clock advances linearly only inside the received patch',()=>{
  assert.equal(interpolateWorldDay(10,11,0,1000),10);
  assert.equal(interpolateWorldDay(10,11,500,1000),10.5);
  assert.equal(interpolateWorldDay(10,11,1500,1000),11);
});

test('calendar marker keeps fractional motion and resets on a new year',()=>{
  assert.deepEqual(calendarPosition(45.5),{whole:45,yearDay:45.5,year:1,month:2,dayOfMonth:16});
  assert.deepEqual(calendarPosition(360),{whole:360,yearDay:0,year:2,month:1,dayOfMonth:1});
});
