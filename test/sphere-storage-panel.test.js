import test from "node:test";
import assert from "node:assert/strict";
import {storageSummary} from "../visualizer/sphere-storage-panel.js";

test("storage summary separates finite indoor capacity from outdoor overflow",()=>{
  assert.equal(storageSummary(null),"Хранение: расчёт ещё не выполнялся");
  const text=storageSummary({usedVolume:2.5,totalCapacity:3,outdoorVolume:1.25});
  assert.match(text,/2\.50 усл\. м³\/3\.00 усл\. м³/);
  assert.match(text,/снаружи 1\.25 усл\. м³/);
});
