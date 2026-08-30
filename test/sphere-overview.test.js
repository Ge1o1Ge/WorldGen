import test from "node:test";
import assert from "node:assert/strict";
import {aggregateResourceEstimates,laborBreakdown,logPosition,productionTiers,summarizeKnowledge} from "../visualizer/sphere-overview.js";

test("resource aggregation supports exact values, uncertain corridors and infinity",()=>{
  const cities=[{stocks:{timber:2,food:1},stockEstimates:{food:{minimum:.5,maximum:Infinity}}},{stocks:{timber:3,food:4}}];
  const rows=aggregateResourceEstimates(cities,["timber","food"]),food=rows.find(row=>row.id==="food"),timber=rows.find(row=>row.id==="timber");
  assert.equal(timber.minimum,5);assert.equal(timber.maximum,5);assert.equal(timber.exact,true);
  assert.equal(food.minimum,4.5);assert.equal(food.maximum,Infinity);assert.equal(food.exact,false);
  assert.equal(logPosition(Infinity),100);assert.ok(logPosition(100)>logPosition(10));
});

test("knowledge and labor collapse multiple settlements into compact summaries",()=>{
  const cities=[{technology:{total:10,known:8,competent:6,capable:5,adopted:4},settlement:{laborAvailableHours:20,laborUsedHours:12,industryLaborHours:2,tasks:[{activity:"water",hours:3},{activity:"hunt",hours:4}]}}];
  assert.deepEqual(summarizeKnowledge(cities),{total:10,known:8,competent:6,capable:5,adopted:4});
  assert.deepEqual(laborBreakdown(cities),{food:4,water:3,construction:0,industry:2,other:5,free:6});
});

test("resource order follows production depth and never jumps with stock amounts",()=>{
  const tiers=productionTiers([
    {inputs:{grain:1},outputs:{flour:1}},
    {inputs:{flour:1,firewood:1},outputs:{food:1}}
  ]),names={grain:"Зерно",flour:"Мука",food:"Еда",firewood:"Дрова"};
  const low=aggregateResourceEstimates([{stocks:{grain:100,flour:2,food:1,firewood:50}}],Object.keys(names),tiers,names);
  const high=aggregateResourceEstimates([{stocks:{grain:1,flour:20,food:100,firewood:5}}],Object.keys(names),tiers,names);
  assert.deepEqual(low.map(item=>item.id),["food","flour","firewood","grain"]);
  assert.deepEqual(high.map(item=>item.id),low.map(item=>item.id));
});
