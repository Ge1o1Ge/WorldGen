import test from "node:test";
import assert from "node:assert/strict";
import {processLines,processSummary} from "../visualizer/sphere-process-panel.js";

test("process summary exposes active work and paid labor",()=>{
  assert.equal(processSummary({}),"Ремесленные процессы: ещё не запускались");
  assert.equal(processSummary({pottery:{batchesToday:2,laborHoursToday:12},cheese:{batchesToday:0,laborHoursToday:0}}),
    "Ремесленные процессы: 1 активных · 12.0 чел·ч сегодня");
});

test("process lines describe real outputs and a blocking constraint",()=>{
  const catalog=[{id:"make_cheese",name:"Домашнее сыроварение",outputs:{cheese:.75}}];
  const [line]=processLines({make_cheese:{batchesToday:.2,totalBatches:4,constraint:"input:milk"}},catalog,{cheese:"тонна"});
  assert.match(line,/Домашнее сыроварение/);
  assert.match(line,/сыр 150\.0 кг/);
  assert.match(line,/нет сырья: milk/);
});

test("small equipment batches remain visible instead of rounding to zero",()=>{
  const catalog=[{id:"pottery",name:"Обжиг",outputs:{pottery_ware:1}}];
  const [line]=processLines({pottery:{batchesToday:.0012,totalBatches:2,constraint:null}},catalog,{pottery_ware:"комплект"});
  assert.match(line,/посуда 0\.001 компл\./);
});
