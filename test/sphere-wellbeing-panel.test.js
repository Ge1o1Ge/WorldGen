import test from "node:test";
import assert from "node:assert/strict";
import {wellbeingSummary,mealText,foodExpectationText,needsText} from "../visualizer/sphere-wellbeing-panel.js";
const rules={foods:[{id:"meat",name:"Мясо"},{id:"fish",name:"Рыба"}]};
test("wellbeing never displays a fictitious happiness score before observation",()=>{
  assert.equal(wellbeingSummary({lastEvaluatedDay:-1,satisfaction:1}),"Потребности: наблюдение начнётся после шага");
  assert.match(wellbeingSummary({lastEvaluatedDay:0,satisfaction:.78,mainConcern:"rest"}),/78%.*свободное время/);
});
test("diet display uses actual consumption and labels unclassified legacy food",()=>{
  const text=mealText({consumedToday:{meat:.003,unknown:.012,fish:0}},rules);
  assert.match(text,/Мясо 3.0 кг/);assert.match(text,/неизвестного состава 12.0 кг/);assert.doesNotMatch(text,/Рыба/);
});
test("unknown tastes are hidden, first taste on day zero and expected shares stay distinct",()=>{
  const text=foodExpectationText({foods:{meat:{firstTastedDay:0,eatenShareToday:.1,expectedShare:.19},fish:{firstTastedDay:null,expectedShare:0}}},rules);
  assert.match(text,/рационе 10.0%, привычная доля 19.0%.*дня 0/);assert.doesNotMatch(text,/Рыба/);
  assert.match(needsText({food:0,rest:.4}),/сытость: 0%.*свободное время: 40%/);
});
