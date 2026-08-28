export const needNames={food:"сытость",water:"вода и её доставка",housing:"качество жилья",variety:"разнообразие пищи",rest:"свободное время",none:"выраженного дефицита нет"};
const percent=n=>`${Math.round(n*100)}%`;
const foodName=(id,rules)=>id==="unknown"?"Еда неизвестного состава":rules?.foods?.find(f=>f.id===id)?.name??id;
export function wellbeingSummary(state){
  if(state.lastEvaluatedDay<0)return "Потребности: наблюдение начнётся после шага";
  return `Удовлетворённость ${percent(state.satisfaction)} · ${needNames[state.mainConcern]??state.mainConcern}`;
}
export function mealText(state,rules){
  const meal=Object.entries(state.consumedToday??{}).filter(([,amount])=>amount>1e-8);
  return "Съедено в поселении за день: "+(meal.map(([id,n])=>`${foodName(id,rules)} ${(n*1000).toFixed(1)} кг`).join(" · ")||"нет данных о потреблённой пище")+".";
}
export function foodExpectationText(profile,rules){
  const known=Object.entries(profile.foods??{}).filter(([,m])=>m.firstTastedDay!=null);
  return known.map(([id,m])=>`${foodName(id,rules)}: в рационе ${(m.eatenShareToday*100).toFixed(1)}%, привычная доля ${(m.expectedShare*100).toFixed(1)}% (пробовали с дня ${m.firstTastedDay})`).join("; ")||"Пищевые привычки пока не наблюдались.";
}
export function needsText(needs){
  return Object.entries(needs??{}).map(([id,n])=>`${needNames[id]??id}: ${percent(n)}`).join(" · ");
}
export function renderWellbeingPanel(city,rules,onFocus){
  const state=city.settlement.wellbeing,box=document.createElement("details"),title=document.createElement("summary");
  box.dataset.panelKey="wellbeing";title.textContent=wellbeingSummary(state);box.append(title);
  const add=text=>{const p=document.createElement("p");p.textContent=text;box.append(p);};
  add(`Учёт опыта с дня ${state.startedDay}. Удовлетворённость — сглаженная сводка; проценты потребностей ниже означают неудовлетворённую долю, а не обеспеченность.`);
  if(state.lastEvaluatedDay<0)return box;
  add(mealText(state,rules));add("Нехватка: "+needsText(state.needs));
  add("Общий рацион распределён между домохозяйствами. Незнакомая пища не создаёт желания; привычки растут постепенно. Эти оценки влияют на занятия и голосование, но не дают дополнительных ресурсов или голосов.");
  for(const [id,p] of Object.entries(state.households??{})){
    const row=document.createElement("details"),heading=document.createElement("summary");row.dataset.panelKey=`household:${id}`;
    const home=(city.homes??[]).find(h=>(h.householdId??h.id)===id&&h.residents>0);
    heading.textContent=`${home?`Дом ${home.x}:${home.y}, место ${home.slot+1}`:"Группа без отдельного дома"} · ${p.members} жителей · ${p.observedDays?percent(p.satisfaction):"наблюдение ещё не началось"}`;
    row.append(heading);
    if(home){const button=document.createElement("button");button.type="button";button.textContent="Показать дом";button.addEventListener("click",()=>{onFocus(home);document.getElementById("sphere-map")?.scrollIntoView({block:"nearest"});});row.append(button);}
    if(p.observedDays){
      for(const text of [
        `Труд ${p.workHours.toFixed(1)}/${p.workCapacityHours.toFixed(1)} чел·ч. ${p.workCapacityHours>0?`Свободно ${percent(p.freeTimeShare)} доступного рабочего времени; ожидают ${percent(p.expectedRest)}.`:"Нет доступного труда; это не считается отдыхом."} Ходьба за водой ${p.waterTravelHours.toFixed(1)} чел·ч.`,
        `Качество жилья ${percent(p.housingQuality)}; привычный уровень ${percent(p.expectedHousing)}. Нехватка: ${needsText(p.needs)}.`,
        foodExpectationText(p,rules)]){const textRow=document.createElement("p");textRow.textContent=text;row.append(textRow);}
    }
    box.append(row);
  }
  return box;
}
