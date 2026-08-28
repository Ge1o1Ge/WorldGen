import test from "node:test";
import assert from "node:assert/strict";
import {proposalSummary,renderCouncilPanel} from "../visualizer/sphere-council-panel.js";

test("decision stages distinguish support, authorization, use and uncertain outcomes",()=>{
  assert.match(proposalSummary({phase:"idea",support:63.4,requiredSupport:100}),/63.4 \/ 100.0.*кворум/);
  assert.match(proposalSummary({phase:"site",requiredSiteSupport:10,leadingDays:1}),/Спор о месте.*1 дн/);
  assert.match(proposalSummary({phase:"approved"}),/ожидает исполнения/);
  assert.match(proposalSummary({phase:"observing",observedDays:10}),/награда пока не выдана/);
  assert.match(proposalSummary({phase:"uncertain"}),/без штрафа/);
});

class Element {
  children=[];textContent="";listeners={};
  constructor(tag){this.tag=tag;}
  append(...items){this.children.push(...items);}
  addEventListener(event,fn){this.listeners[event]=fn;}
  all(){return [this,...this.children.flatMap(c=>c.all())];}
}
test("council shows separate attention and weighted influence; site buttons only focus",t=>{
  const original=globalThis.document;globalThis.document={createElement:tag=>new Element(tag)};
  t.after(()=>{if(original===undefined)delete globalThis.document;else globalThis.document=original;});
  const site={id:"cell-a",face:"NegativeX",x:1,y:3,available:true,support:45};let focused;
  const council={lastDay:5,households:4,spentToday:100,issuedToday:100,weightedToday:150,reputation:{water:{minimum:.9,maximum:1.1}},
    proposals:[{id:"a",reason:"Нужен колодец",createdDay:2,phase:"site",requiredSiteSupport:50,leadingDays:0,sites:[site]}]};
  const panel=renderCouncilPanel({council,onFocus:p=>focused=p});
  const text=panel.all().map(n=>n.textContent).join(" ");
  assert.match(text,/100\/100/);assert.match(text,/150.0/);assert.match(text,/вода 0.90–1.10/);
  const button=panel.all().find(n=>n.tag==="button");assert.match(button.title,/не голос/);
  button.listeners.click();assert.equal(focused,site);assert.equal(site.support,45);
});
