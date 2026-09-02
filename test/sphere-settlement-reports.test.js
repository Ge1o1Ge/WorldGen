import test from "node:test";
import assert from "node:assert/strict";
import {aggregateHerds,aggregateScouting,structuredJournalEvents} from "../visualizer/sphere-settlement-reports.js";

test("livestock report aggregates species and keeps demographic outcomes",()=>{
  const rows=aggregateHerds([
    {biology:{herds:{rabbit:{females:1,males:1,young:[{count:2}],count:4,captured:2,births:2,deaths:1,slaughtered:1,health:.75,pasture:{x:1},pastureWork:24}}}},
    {biology:{herds:{rabbit:{females:1,males:0,young:[],count:1,captured:1,births:0,deaths:0,slaughtered:0,health:1,pasture:{x:2},pastureWork:4}}}}
  ]);
  assert.equal(rows.length,1);const rabbit=rows[0];assert.equal(rabbit.count,5);assert.equal(rabbit.young,2);assert.equal(rabbit.captured,3);
  assert.equal(rabbit.activePastures,1);assert.equal(rabbit.preparingPastures,1);assert.equal(rabbit.deaths+rabbit.slaughtered,2);
});

test("scouting report distinguishes delivered animals that still exist in a herd",()=>{
  const summary=aggregateScouting([{id:"camp",name:"Очаг",biology:{herds:{rabbit:{count:1}}},scoutReports:[{
    receivedDay:30,durationDays:4,routeCells:12,surveyedCells:7,plants:["wheat"],animals:["rabbit"],capturedAnimals:{rabbit:1,chicken:1}
  }]}],[]);
  assert.equal(summary.totals.surveyed,7);assert.equal(summary.totals.captured,2);
  assert.deepEqual(summary.reports[0].established,["rabbit"]);
  assert.ok(structuredJournalEvents.has("crop_loss"));assert.ok(structuredJournalEvents.has("herd_death"));
});
