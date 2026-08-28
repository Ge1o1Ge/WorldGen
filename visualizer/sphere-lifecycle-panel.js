import {buildingNames,buildingStates} from "./settlement-symbols.js";
const percent=n=>`${Math.round(n*100)}%`;
const resources={timber:"дерево",clay:"глина",stone:"камень",fiber:"волокно"};
export const materialAmounts=amounts=>Object.entries(amounts??{}).filter(([,n])=>n>1e-9).map(([id,n])=>`${resources[id]??id} ${(n*1000).toFixed(1)} кг`).join(", ")||"нет";
export function buildingHoverText(b){
  let text=`${buildingNames[b.kind]??b.kind} · ${buildingStates[b.status]??b.status}`;
  if(b.lifecycle)text+=` · износ: ремонтируемый ${percent(b.lifecycle.repairableWear)}, необратимый ${percent(b.lifecycle.permanentWear)}`;
  if(b.well)text+=` · вода ${(b.well.stock*1000).toFixed(0)}/${(b.well.capacity*1000).toFixed(0)} л`;
  if(b.field)text+=` · почва ${percent(b.soilQuality)}${b.field.fallowSinceDay!=null?" · залежь":""}`;
  return text;
}
export function buildingConditionText(b,rules){
  const parts=[buildingNames[b.kind]??b.kind,buildingStates[b.status]??b.status];
  if(b.lifecycle){const l=b.lifecycle;
    parts.push(rules?.materials?.find(m=>m.id===l.material)?.name??l.material,`возраст учёта ${(l.ageDays/365).toFixed(1)} г.`,
      `ремонтируемый износ ${percent(l.repairableWear)}`,`необратимый ${percent(l.permanentWear)}`,`эффективность ${percent(l.efficiency)}`);
    if(l.baselineAssessment)parts.push(`история до дня ${l.accountedFromDay} не учитывалась`);
    if(l.retiring)parts.push("готовится к освобождению");
  }
  if(b.well)parts.push(`вода ${(b.well.stock*1000).toFixed(0)}/${(b.well.capacity*1000).toFixed(0)} л`,
    `приток до ${(b.well.rechargeRate*1000).toFixed(0)} л/день`, `взято ${(b.well.withdrawnToday*1000).toFixed(0)} л`);
  if(b.field)parts.push(`почва ${percent(b.soilQuality)}`,b.field.fallowSinceDay!=null?`залежь с дня ${b.field.fallowSinceDay}`:
    `ожидаемая отдача ${(b.field.expectedOutputPerHour*1000).toFixed(2)} кг/ч с дорогой`);
  return parts.join(" · ");
}
export function renderLifecyclePanel(city,rules,onFocus){
  const m=city.settlement.maintenance??{repairHours:0,demolitionHours:0,meanEfficiency:1,replacementNeeded:0,demolished:0,fallowFields:0},box=document.createElement("details"),title=document.createElement("summary");
  title.textContent=city.settlement.maintenance?`Содержание: ремонт ${m.repairHours.toFixed(1)} ч · снос ${m.demolitionHours.toFixed(1)} ч · эффективность ${percent(m.meanEfficiency)}`:
    "Содержание: начальная оценка · суточный расчёт после шага";
  const expenses=document.createElement("p");expenses.textContent=`Ремонт за день: ${materialAmounts(m.materialsUsed)}. Возвращено при разборе: ${materialAmounts(m.salvaged)}. Требуют замены: ${m.replacementNeeded}; разобрано: ${m.demolished}; залежей: ${m.fallowFields}.`;
  const explanation=document.createElement("p");explanation.textContent="Ремонт убирает только ремонтируемый износ. Старение конструкции необратимо. Вода пополняется постепенно; почва восстанавливается без обработки.";
  box.append(title,expenses,explanation);
  for(const home of city.homes??[]){const row=document.createElement("p"),button=document.createElement("button");
    button.type="button";button.textContent=`${home.x}:${home.y}`;button.addEventListener("click",()=>onFocus(home));
    row.append(button,document.createTextNode(" "+buildingConditionText(home,rules)));box.append(row);}
  return box;
}
