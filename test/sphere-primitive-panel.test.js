import test from "node:test";
import assert from "node:assert/strict";
import {primitiveLines} from "../visualizer/sphere-primitive-panel.js";

test("legacy settlements do not receive invented weather or equipment",()=>{
  assert.deepEqual(primitiveLines({settlement:{}}),[]);
});
test("primitive panel separates fresh and winter food and shows actual gear",()=>{
  const lines=primitiveLines({population:100,stocks:{food:5,winter_food:1,stone_kit:8,primitive_bow:2,garments:99},settlement:{primitive:{
    weather:{temperatureC:-2,rainMm:1,soilWater:.4,snow:12},storedFoodTarget:3,preservedToday:.02,releasedToday:.01,
    herdBiomass:.2,herdCareHours:8,herdFeedToday:.001,representative:"home:1"}}});
  assert.ok(lines.some(s=>s.includes("-2.0 °C")));
  assert.ok(lines.some(s=>s.includes("1000 кг")&&s.includes("10.0 дн.")));
  assert.ok(lines.some(s=>s.includes("8.0 каменных комплектов")));
  assert.ok(lines.some(s=>s.includes("делегируется из существующего бюджета")));
  assert.ok(lines.every(s=>!s.includes("NaN")));
});
