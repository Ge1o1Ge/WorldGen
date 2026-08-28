import test from "node:test";
import assert from "node:assert/strict";
import {buildingConditionText,materialAmounts} from "../visualizer/sphere-lifecycle-panel.js";
test("building details distinguish reversible wear, irreversible age and real well stocks",()=>{
  const text=buildingConditionText({kind:"well",status:"active",lifecycle:{material:"wood",ageDays:1095,repairableWear:.15,permanentWear:.05,efficiency:.8},
    well:{stock:.1,capacity:1.2,rechargeRate:.7,withdrawnToday:.6}},{materials:[{id:"wood",name:"Дерево"}]});
  assert.match(text,/ремонтируемый износ 15%/);assert.match(text,/необратимый 5%/);assert.match(text,/эффективность 80%/);
  assert.match(text,/100\/1200 л/);assert.match(text,/700 л\/день/);
});
test("fallow land reports current soil instead of a new pristine plot",()=>{
  assert.match(buildingConditionText({kind:"garden",status:"abandoned",soilQuality:.2,field:{fallowSinceDay:1400}}),/почва 20%.*залежь с дня 1400/);
  assert.equal(materialAmounts({timber:.002,stone:0}),"дерево 2.0 кг");
});
