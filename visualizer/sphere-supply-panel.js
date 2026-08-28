// City knowledge contains returned reports only. Live expedition positions belong
// to the observer view; they are not an omniscient input to the city's decisions.
export function supplySummary(supply,rules) {
  if(!supply.history.length)return "Наблюдения о снабжении начнутся со следующего дня.";
  return `Снабжение: ${Math.round(supply.laborShare*100)}% доступного труда за ${supply.history.length} дн. · `+
    `${supply.accessibleCells} знакомых участков. `+
    (supply.pressureStreak?`Ухудшение: ${Math.min(supply.pressureStreak,rules.pressureDays)}/${rules.pressureDays} дн. до решения.`:"Устойчивого ухудшения нет.");
}

export function renderSupplyPanel({city,day,rules,scout,onFocus}) {
  const supply=city.settlement.supply,section=document.createElement("section");
  section.className="sphere-supply";
  const title=document.createElement("strong");title.textContent="Снабжение и разведка";
  const summary=document.createElement("p");summary.textContent=supplySummary(supply,rules);
  const reason=document.createElement("p");reason.textContent=`${supply.reason}. ${supply.action}.`;
  section.append(title,summary,reason);
  const food=city.settlement.food;
  if(food){
    const production=document.createElement("p"),travel=document.createElement("p"),gardens=document.createElement("p");
    const rate=food.wildHours>0?`${(food.wildOutput*1000/food.wildHours).toFixed(2)} кг/ч`:"нет добычи";
    production.textContent=`Пища: ${(food.wildOutput*1000).toFixed(1)} кг из природы · ${(food.gardenOutput*1000).toFixed(1)} кг с огородов. Добыча с учётом поиска и пути: ${rate}.`;
    travel.textContent=`На пищу ${food.laborHours.toFixed(0)} чел·ч, из них путь ${food.travelHours.toFixed(1)} ч. Средняя трудовзвешенная дальность по стоимости пути: ${food.meanOneWayMeters.toFixed(0)} м в одну сторону.`;
    gardens.textContent=city.biology?
      `Поля: ${food.readyGardens} освоены, ${food.preparingGardens} обустраиваются. Пищу даёт собранный и подготовленный урожай; посев, созревание и сезонный покой показаны в карточке растений.`:
      `Огороды: ${food.readyGardens} дают урожай, ${food.preparingGardens} готовятся или растут. Урожай ограничен площадью; пока используется среднесуточная отдача без сезонного цикла.`;
    section.append(production,travel,gardens);
    if(food.movedToday){const moving=document.createElement("p");moving.textContent=`Сегодня переселились ${food.movedToday} жителей; оставшиеся продолжают жить в прежнем доме.`;section.append(moving);}
  }
  if(supply.history.length){
    const estimate=document.createElement("p");
    estimate.textContent=`Верхняя оценка природного восстановления пищи: ${(supply.foodRenewalPerDay*1000).toFixed(1)} кг/день. `+
      (city.biology?"Это оценка по текущей погоде, не гарантированная добыча: труд, остатки и доступность ограничивают результат.":
      "Это не гарантированная добыча: труд, остатки и доступность ограничивают результат; сезонный цикл ещё не учтён.");
    section.append(estimate);
  }
  if(scout&&scout.phase!=="returned"){
    const expedition=document.createElement("p");
    expedition.textContent=`Наблюдатель · ${scout.people} разведчика: ${scout.phase==="returning"?"возвращаются":"обследуют направление"}, `+
      `вышли в день ${scout.departureDay}; обследовано ${scout.traversedCells} участков. `+
      `В походе: ${(scout.food*1000).toFixed(1)} кг еды, ${(scout.water*1000).toFixed(0)} л воды. `+
      `На группу выделено ${supply.scoutLaborHours.toFixed(0)} чел·ч сегодня. Отчёт ещё не доставлен.`;
    const focus=document.createElement("button");focus.type="button";focus.textContent="Место группы · наблюдатель";
    focus.addEventListener("click",()=>onFocus(scout));section.append(expedition,focus);
  }
  const reports=city.scoutReports??[];
  if(reports.length){
    const reportsTitle=document.createElement("p");reportsTitle.textContent=`Полученные отчёты: ${reports.length} (последние ${rules.maximumReports}).`;
    section.append(reportsTitle);
    // Keep the latest report visible while the simulation updates. A collapsed
    // details element recreated on each day would continually lose user state.
    const report=reports.at(-1),text=document.createElement("p");
    text.textContent=`День ${report.receivedDay}, ${day-report.receivedDay} дн. назад: ${report.outcome}. `+
      `Обследовано за пределами домашней окрестности: ${report.surveyedCells} участков.`;
    section.append(text);
    for(const candidate of report.candidates){
      const button=document.createElement("button");button.type="button";
      button.textContent=`Участок ${candidate.x}:${candidate.y} · наблюдение дня ${candidate.observedDay}`;
      button.title="Сведения о воде и природном потенциале на дату посещения; пригодность всей окрестности ещё не оценена.";
      button.addEventListener("click",()=>onFocus(candidate));section.append(button);
    }
  }else{
    const empty=document.createElement("p");empty.textContent="Доставленных отчётов пока нет. Решения не используют неизвестную часть карты.";section.append(empty);
  }
  return section;
}
