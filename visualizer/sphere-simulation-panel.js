import {buildingNames,buildingStates} from "./settlement-symbols.js";
import {SimulationPlayback} from "./simulation-playback.js";
import {renderSupplyPanel} from "./sphere-supply-panel.js";
import {renderCouncilPanel} from "./sphere-council-panel.js";
import {wildlifeSummary} from "./sphere-wildlife.js";
import {renderLifecyclePanel} from "./sphere-lifecycle-panel.js";
import {renderWellbeingPanel} from "./sphere-wellbeing-panel.js";
import {renderPrimitivePanel} from "./sphere-primitive-panel.js";
import {renderBiologyPanel} from './sphere-biology.js';

export async function connectSphereSimulation({onState,onFocus,biosphere,mapQuery=()=>""}) {
  let state=null;
  const one=document.getElementById("sphere-step-one"),month=document.getElementById("sphere-step-month"),play=document.getElementById("sphere-play");
  const status=document.getElementById("sphere-sim-status"),sites=document.getElementById("sphere-industry");
  const homes=document.getElementById("sphere-home");
  const playback=new SimulationPlayback({advance:step,onChange:controls,onError:error=>{
    status.textContent=`Симуляция остановлена: ${error.message}. После сетевой ошибки обновите страницу: запрос мог уже выполниться.`;
  }});
  function controls(){
    one.disabled=month.disabled=playback.busy||!state;play.disabled=!state;
    play.textContent=playback.playing?"Пауза":"Запустить";
    document.getElementById("sphere-playback-status").textContent=playback.playing
      ? (playback.busy?"Автопрогон · расчёт дня…":"Автопрогон · до нажатия «Пауза»")
      : playback.busy?"Завершается текущий шаг…":"На паузе";
  }
  function render(next) {
    if(state&&next.revision<state.revision)return;
    if(onState(next)===false)return;state=next;
    document.getElementById("sphere-day").textContent=String(next.day);
    document.getElementById("sphere-wildlife-status").textContent=wildlifeSummary(next.wildlife??[]);
    document.getElementById("sphere-name").textContent=next.name;
    document.getElementById("sphere-active-zones").textContent=next.activeZones.toLocaleString("ru-RU");
    const cards=document.getElementById("sphere-economy-cities");
    const expanded=new Set([...cards.querySelectorAll("details[data-panel-key][open]")].map(d=>`${d.closest("[data-city-id]").dataset.cityId}:${d.dataset.panelKey}`));
    cards.replaceChildren();
    for(const city of next.cities) {
      const card=document.createElement("div");card.className="sphere-city-economy";
      card.dataset.cityId=city.id;
      const title=document.createElement("strong");title.textContent=city.name;
      const summary=document.createElement("p");summary.textContent=`${city.population.toLocaleString("ru-RU")} жителей · ${city.settlement?.primitive?"свежая еда":"еда"} на ${city.foodDays.toFixed(1)} дн. · здоровье ${Math.round(city.health*100)}%`;
      const life=city.settlement;
      const detail=document.createElement("p");detail.textContent=life?`Жильё: ${city.population}/${life.housingCapacity} мест · без жилья ${life.unhoused} · вода за день ${next.day?Math.round(life.waterCoverage*100)+"%":"—"}${city.shortage?" · НЕХВАТКА ЕДЫ":""}`:
        `${city.industries.length} производств · ${city.technologyCount} технологий`;
      const stocks=document.createElement("p");stocks.textContent=`Запасы: еда ${(city.stocks.food*1000).toFixed(0)} кг, вода ${((city.stocks.water??0)*1000).toFixed(0)} л, древесина ${(city.stocks.timber*1000).toFixed(0)} кг, ткань ${((city.stocks.cloth??0)*1000).toFixed(1)} кг`;
      card.append(title,summary,detail,stocks);cards.append(card);
      if(life){
        const labor=document.createElement("p");labor.textContent=next.day?`Труд: ${(life.laborUsedHours+life.industryLaborHours).toFixed(0)}/${life.laborAvailableHours.toFixed(0)} чел·ч; из них ходьба за водой ${life.waterTravelHours.toFixed(1)} ч.`:"Суточный расчёт ещё не выполнялся.";
        const production=document.createElement("p"),names={food:"еда",water:"вода",timber:"древесина",firewood:"топливо",fiber:"волокно",cloth:"ткань"};
        production.textContent="Быт за день: "+(Object.entries(life.production).filter(([,n])=>n>1e-6).map(([id,n])=>`${names[id]??({stone_kit:"каменные комплекты",primitive_bow:"луки",garments:"одежда",hides:"шкуры"})[id]??id} ${(next.resourceUnits?.[id]==="комплект"?n:n*1000).toFixed(1)} ${next.resourceUnits?.[id]==="комплект"?"компл.":id==="water"?"л":"кг"}`).join(" · ")||"—");
        const skills=document.createElement("p");skills.textContent="Практические навыки: "+(life.discoveries.map(id=>id==="masonry"?"Каменная кладка":next.discoveryNames[id]??id).join(", ")||"охота, собирательство, прибрежный лов");
        const knowledge=document.createElement("p"),known=city.worldKnowledge?.settlements.filter(place=>place.cityId!==city.id)??[];
        knowledge.textContent="Сведения о мире: "+(known.length?known.map(place=>`${place.name} (сведения дня ${place.observedDay}, получены ${place.receivedDay})`).join("; "):"другие поселения пока неизвестны")+
          ` · известных событий ${city.worldKnowledge?.observationCount??0}.`;
        const decision=document.createElement("p");decision.textContent=`Решение: ${life.decision}`;
        const tasks=document.createElement("details"),taskTitle=document.createElement("summary"),taskList=document.createElement("ul");taskTitle.textContent="Занятия домохозяйств";
        const activityHours=new Map();for(const task of life.tasks)activityHours.set(task.activity,(activityHours.get(task.activity)??0)+task.hours);
        for(const [id,hours] of activityHours){const row=document.createElement("li");row.textContent=`${({water:"Доставка воды",cultivate:"Уход за освоенными огородами",move:"Переезд частями",repair:"Ремонт",demolition:"Разбор строений",clay:"Заготовка глины",stone:"Заготовка камня"})[id]??next.activityNames[id]??id}: ${hours.toFixed(1)} чел·ч`;taskList.append(row);}
        tasks.append(taskTitle,taskList);card.append(labor,production,skills,knowledge,decision,tasks);
        if(life.primitive)card.append(renderPrimitivePanel(city));
        if(city.biology&&biosphere)card.append(renderBiologyPanel(city,biosphere));
        if(life.wellbeing)card.append(renderWellbeingPanel(city,next.wellbeingRules,onFocus));
        if(life.maintenance||next.lifecycleRules)card.append(renderLifecyclePanel(city,next.lifecycleRules,onFocus));
        if(city.council)card.append(renderCouncilPanel({council:city.council,onFocus}));
        if(life.supply)card.append(renderSupplyPanel({city,day:next.day,rules:next.explorationRules,
          scout:next.scouts?.find(e=>e.cityId===city.id),onFocus}));
      }
    }
    for(const panel of cards.querySelectorAll("details[data-panel-key]"))panel.open=expanded.has(`${panel.closest("[data-city-id]").dataset.cityId}:${panel.dataset.panelKey}`);
    const selected=sites.value;sites.replaceChildren();
    const empty=document.createElement("option");empty.value="";empty.textContent="Выбрать производство…";sites.append(empty);
    for(const city of next.cities){const group=document.createElement("optgroup");group.label=city.name;
      for(const industry of city.industries){const option=document.createElement("option");option.value=industry.id;
        option.textContent=`${industry.name} · ${industry.totalBatches.toFixed(1)} партий${industry.blockedReason?" · остановлено":""}`;group.append(option);}
      sites.append(group);}
    sites.value=selected;
    sites.closest("label").hidden=!next.cities.some(city=>city.industries.length);
    const selectedHome=homes.value;homes.replaceChildren();
    const homeEmpty=document.createElement("option");homeEmpty.value="";homeEmpty.textContent="Жильё, огороды, стройки и колодцы…";homes.append(homeEmpty);
    for(const city of next.cities){const group=document.createElement("optgroup");group.label=city.name;
      for(const home of city.homes??[]){const option=document.createElement("option");option.value=home.id;
        const condition=home.kind==="garden"&&home.status==="active"&&home.readyDay>next.day?`растёт до дня ${home.readyDay}`:buildingStates[home.status];
        option.textContent=`${buildingNames[home.kind]??home.kind} · ${condition}${home.kind==="house"?` · ${home.residents}/${next.residentsPerHouse}${home.moveFinished===false?" · идёт переезд":""}`:""} · ${home.x}:${home.y}${home.kind==="garden"?" · целая зона":` · место ${home.slot+1}`}`;group.append(option);}homes.append(group);}
    homes.value=selectedHome;
    const events=document.getElementById("sphere-sim-events");events.replaceChildren();
    const eventNames={household_discovery:"Освоен новый навык",settlement_building_started:"Начато строительство",settlement_building_completed:"Постройка готова",settlement_building_abandoned:"Постройка заброшена",household_relocated:"Домохозяйство переселилось",
      household_move_prepared:"Дом для постепенного переезда готов",supply_pressure:"Устойчивое ухудшение снабжения",supply_pressure_relieved:"Снабжение восстановилось",scouting_departed:"Разведчики вышли в путь",scouting_returned:"Получен отчёт разведки",simulation_rules_updated:"Обновлены правила симуляции",
      decision_proposed:"Предложен проект",decision_stage_changed:"Обсуждение проекта",decision_assessed:"Оценена польза решения",settlement_building_demolished:"Постройка разобрана",lightning_fire:"Пожар от молнии",
      plant_discovered:"Найдено съедобное растение",crop_sown:"Выполнен посев",crop_harvest:"Собран урожай",crop_failed:"Посев погиб",
      animal_captured:"Живой отлов",herd_birth:"Приплод стада",herd_death:"Потеря животного",pasture_ready:"Пастбище освоено",
      resource_camp_planned:"Выбран промысловый участок",resource_camp_ready:"Промысловый лагерь готов",resource_camp_abandoned:"Промысловый лагерь заброшен"};
    for(const event of next.events.slice(0,8)){const row=document.createElement("li");
      const speciesId=event.details?.crop??event.details?.species;
      const speciesName=biosphere&&[...biosphere.crops,...biosphere.animals].find(s=>s.id===speciesId)?.name;
      row.textContent=`День ${event.day}: ${eventNames[event.type]??event.type} · ${event.details?.name??speciesName??event.details?.reason??event.subjectId??"мир"}`;events.append(row);}
    status.textContent=(next.warnings.length?next.warnings.join(". "):
      `Работает C#-симуляция на сфере. ${next.atmosphere?`Погода: ${next.atmosphere.systems} атмосферных систем; горит клеток: ${next.atmosphere.burningCells}.`:`Грузы в пути: ${next.shipments}.`}`)+" Изменения пока в памяти сервера.";
    if(next.trailSummary)document.getElementById("sphere-trail-status").textContent=
      `Тропы: ${next.trailSummary.visible} видимых участков, сегодня использовано ${next.trailSummary.usedToday}. Неиспользуемые зарастают.`;
  }
  async function step(days) {
      const response=await fetch(`/api/sphere/step?days=${days}&${mapQuery()}`,{method:"POST"});
      const next=await response.json();if(!response.ok)throw new Error(next.error??`HTTP ${response.status}`);
      render(next);
  }
  one.addEventListener("click",()=>playback.step(1));month.addEventListener("click",()=>playback.step(30));
  play.addEventListener("click",()=>playback.toggle());
  sites.addEventListener("change",()=>{const industry=state?.cities.flatMap(city=>city.industries).find(item=>item.id===sites.value);if(industry)onFocus(industry);});
  homes.addEventListener("change",()=>{const home=state?.cities.flatMap(city=>city.homes??[]).find(item=>item.id===homes.value);if(home)onFocus(home);});
  // A hidden embedded panel may still be the user's active simulation. Only an
  // explicit pause or actually leaving this document stops playback.
  window.addEventListener("pagehide",()=>playback.pause());
  try {const response=await fetch(`/api/sphere/simulation?${mapQuery()}`,{cache:"no-store"});if(!response.ok)throw new Error(`HTTP ${response.status}`);render(await response.json());controls();}
  catch(error){one.disabled=month.disabled=play.disabled=true;status.textContent=`Не удалось загрузить симуляцию: ${error.message}`;}
}
