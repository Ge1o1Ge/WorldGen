import test from "node:test";
import assert from "node:assert/strict";
import { SphereChunkCache } from "../visualizer/sphere-chunks.js";

const settle = () => new Promise(resolve => setImmediate(resolve));
test("чанки: загрузка ограничена, ненужная очередь не исполняется", async () => {
  const requests = [];
  const cache = new SphereChunkCache({ concurrency: 2, capacity: 3,
    fetchChunk: key => new Promise(resolve => requests.push({key, resolve})) });
  cache.setDesired([1,2,3,4]);
  await settle();
  assert.equal(requests.length, 2);
  cache.setDesired([5]);
  requests[0].resolve({key:1}); requests[1].resolve({key:2});
  await settle();
  assert.deepEqual(requests.map(r => r.key), [1,2,5]);
  requests[2].resolve({key:5});
  await settle();
  assert.equal(cache.status.loaded, 1);
  assert.ok(cache.status.resident <= 3);
});
test("чанки: устаревшая версия не попадает в кэш; ошибка требует явного повтора", async () => {
  const requests = [];
  const cache = new SphereChunkCache({concurrency:1, fetchChunk: key =>
    new Promise((resolve, reject) => requests.push({key, resolve, reject}))});
  cache.setDesired([1]); await settle();
  cache.invalidate();
  requests[0].resolve({old:true}); await settle();
  assert.equal(cache.get(1), undefined);
  requests[1].reject(new Error("offline")); await settle();
  assert.equal(cache.status.failed, 1);
  assert.equal(requests.length, 2);
  cache.retry(); await settle();
  requests[2].resolve({old:false}); await settle();
  assert.equal(cache.get(1).old, false);
});
