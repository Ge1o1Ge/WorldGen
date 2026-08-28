import test from "node:test";
import assert from "node:assert/strict";
import {supplySummary,renderSupplyPanel} from "../visualizer/sphere-supply-panel.js";

const rules={pressureDays:7,maximumReports:6};
const supply={history:[],reason:"Наблюдение",action:"Ждём",laborShare:.21,accessibleCells:140,
  pressureStreak:0,foodRenewalPerDay:.12,scoutLaborHours:12};
test("supply summary distinguishes missing observations, healthy state and sustained pressure",()=>{
  assert.match(supplySummary(supply,rules),/со следующего дня/);
  const measured={...supply,history:Array(14).fill({})};
  assert.match(supplySummary(measured,rules),/21%.*14 дн.*140.*ухудшения нет/);
  assert.match(supplySummary({...measured,pressureStreak:20},rules),/7\/7/);
});

// A minimal DOM checks text-only rendering and coordinate handoff; browser QA
// separately checks the actual layout and integration with the map.
class Element {
  children=[];textContent="";listeners={};
  constructor(tag){this.tag=tag;}
  append(...items){this.children.push(...items);}
  addEventListener(name,fn){this.listeners[name]=fn;}
  all(){return [this,...this.children.flatMap(c=>c.all())];}
}
test("active expedition is explicitly observer-only and no report is invented",t=>{
  const original=globalThis.document;globalThis.document={createElement:tag=>new Element(tag)};
  t.after(()=>{if(original===undefined)delete globalThis.document;else globalThis.document=original;});
  const scout={id:"a",cityId:"city",phase:"outbound",people:2,departureDay:8,traversedCells:24,
    food:.01,water:.05,face:"PositiveX",x:12,y:8,observations:[{name:"SECRET"}]};
  let focus;
  const panel=renderSupplyPanel({city:{settlement:{supply},scoutReports:[]},day:8,rules,scout,onFocus:p=>focus=p});
  const text=panel.all().map(n=>n.textContent).join(" ");
  assert.match(text,/Наблюдатель/);assert.match(text,/Отчёт ещё не доставлен/);
  assert.match(text,/Доставленных отчётов пока нет/);assert.doesNotMatch(text,/SECRET/);
  panel.all().find(n=>n.tag==="button").listeners.click();assert.equal(focus,scout);
});
test("only returned dated reports provide site focus and preserve face coordinates",t=>{
  const original=globalThis.document;globalThis.document={createElement:tag=>new Element(tag)};
  t.after(()=>{if(original===undefined)delete globalThis.document;else globalThis.document=original;});
  const candidate={face:"NegativeZ",x:900,y:2,observedDay:9};let focus;
  const report={receivedDay:11,surveyedCells:14,outcome:"Найден участок",candidates:[candidate]};
  const panel=renderSupplyPanel({city:{settlement:{supply},scoutReports:[report]},day:15,rules,
    scout:{phase:"returned"},onFocus:p=>focus=p});
  const text=panel.all().map(n=>n.textContent).join(" ");
  assert.match(text,/День 11, 4 дн. назад/);assert.match(text,/наблюдение дня 9/);
  assert.doesNotMatch(text,/Отчёт ещё не доставлен/);
  panel.all().find(n=>n.tag==="button").listeners.click();assert.equal(focus,candidate);
});

test("food panel exposes actual productivity, prepared land and gradual relocation",t=>{
  const original=globalThis.document;globalThis.document={createElement:tag=>new Element(tag)};
  t.after(()=>{if(original===undefined)delete globalThis.document;else globalThis.document=original;});
  const food={wildOutput:.012,wildHours:24,gardenOutput:.04,laborHours:50,travelHours:6,
    meanOneWayMeters:900,readyGardens:3,preparingGardens:2,movedToday:3};
  const panel=renderSupplyPanel({city:{settlement:{supply,food},scoutReports:[]},day:100,rules,onFocus:()=>{}});
  const text=panel.all().map(n=>n.textContent).join(" ");
  assert.match(text,/0.50 кг\/ч/);assert.match(text,/900 м/);
  assert.match(text,/3 дают урожай, 2 готовятся/);assert.match(text,/переселились 3 жителей/);
});
