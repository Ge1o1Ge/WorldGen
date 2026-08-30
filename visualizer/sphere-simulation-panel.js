import {buildingNames,buildingStates} from "./settlement-symbols.js";
import {SimulationLiveChannel,mergeLiveSimulationState} from "./simulation-live.js?v=live1";
import {aggregateResourceEstimates,laborBreakdown,logPosition,productionTiers,summarizeKnowledge} from "./sphere-overview.js?v=overview2";
import {renderSupplyPanel} from "./sphere-supply-panel.js";
import {renderCouncilPanel} from "./sphere-council-panel.js";
import {wildlifeSummary} from "./sphere-wildlife.js";
import {renderLifecyclePanel} from "./sphere-lifecycle-panel.js";
import {renderWellbeingPanel} from "./sphere-wellbeing-panel.js";
import {renderPrimitivePanel} from "./sphere-primitive-panel.js";
import {renderBiologyPanel} from './sphere-biology.js';
import {renderStoragePanel} from './sphere-storage-panel.js';
import {renderProcessPanel} from './sphere-process-panel.js';

export function plantDiscoveryName(event,biosphere){
  if(event?.type!=="plant_discovered")return null;
  const speciesId=event.details?.crop??event.details?.species;
  const plant=biosphere?.crops?.find(species=>species.id===speciesId);
  if(!plant)return "Найдено растение";
  return Number(plant.foodValue)>0?"Найдено съедобное растение":"Найдено техническое растение";
}

export async function connectSphereSimulation({onState,onFocus,biosphere,processes=[],activities=[],mapQuery=()=>"",scheduleUi=work=>work(),getScope=()=>({key:"world",level:"world",cityIds:null,label:"Весь мир",context:"Все поселения"})}) {
  let state=null;
  let lastScopeKey="";
  const resourceTiers=productionTiers(processes,activities);
  const speedButtons=[...document.querySelectorAll(".sphere-speed")],play=document.getElementById("sphere-play");
  const status=document.getElementById("sphere-sim-status"),sites=document.getElementById("sphere-industry");
  const homes=document.getElementById("sphere-home");
  let liveFrame=0,pendingLive=null;
  const socketUrl=`${location.protocol==="https:"?"wss":"ws"}://${location.host}/api/sphere/live`;
  const channel=new SimulationLiveChannel({url:socketUrl,onStatus:()=>controls(),onError:error=>{
    status.textContent=`Поток симуляции остановлен: ${error.message}. Мир больше не продвигается этой вкладкой.`;controls();
  },onMessage:async(message,bytes)=>{
    if(message.type==="state"){
      const playbackStatus=document.getElementById("sphere-playback-status");playbackStatus.dataset.serverDay=String(message.day);playbackStatus.dataset.serverRevision=String(message.revision);
      pendingLive={message,bytes};
      if(!liveFrame)liveFrame=requestAnimationFrame(()=>{
        liveFrame=0;const update=pendingLive;pendingLive=null;
        state=mergeLiveSimulationState(state,update.message);render(state,{live:true});
        const playbackStatus=document.getElementById("sphere-playback-status");playbackStatus.dataset.liveBytes=String(update.bytes);playbackStatus.dataset.renderedDay=String(state.day);
        channel.acknowledge();
      });
    }else if(message.type==="paused"){
      document.getElementById("sphere-playback-status").textContent="Синхронизация полной сводки…";
      await syncFull();
    }else if(message.type==="busy"){
      status.textContent=message.message;controls();
    }
  }});
  try{channel.setSpeed(Number(localStorage.getItem("worldgen.simulationSpeed"))||1);}catch{channel.setSpeed(1);}
  function controls(){
    for(const button of speedButtons){const active=Number(button.dataset.speed)===channel.speed;button.disabled=!state||!channel.ready;button.classList.toggle("is-active",active);button.setAttribute("aria-pressed",String(active));}
    play.disabled=!state||!channel.ready;play.textContent=channel.playing?"❚❚":"▶";
    play.setAttribute("aria-label",channel.playing?"Поставить симуляцию на паузу":"Запустить симуляцию");play.title=play.getAttribute("aria-label");
    document.getElementById("sphere-playback-status").textContent=channel.playing?`Поток ${channel.speed}× · ${channel.speed} дн. каждые 5 с`:channel.ready?"На паузе":"Подключение потока…";
  }
  function renderOverview(next,cities,scope){
    const host=document.getElementById("sphere-area-resources");host.replaceChildren();
    const population=cities.reduce((sum,city)=>sum+city.population,0);
    const weighted=(read,fallback=0)=>population?cities.reduce((sum,city)=>sum+read(city)*city.population,0)/population:fallback;
    const foodDays=weighted(city=>city.foodDays),water=weighted(city=>city.settlement?.waterCoverage??0),health=weighted(city=>city.health);
    const housingPopulation=cities.reduce((sum,city)=>sum+Math.min(city.population,city.settlement?.housingCapacity??0),0);
    const timber=cities.reduce((sum,city)=>sum+(city.stocks.timber??0)+(city.stocks.firewood??0),0);
    const items=[
      ["Население",population.toLocaleString("ru-RU"),`${cities.length} пос.`,""],
      ["Пища",`${foodDays.toFixed(1)} дн.`,`средний запас`,foodDays<5?"is-crisis":foodDays<14?"is-warning":""],
      ["Вода",`${Math.round(water*100)}%`,`суточная обеспеченность`,water<.75?"is-crisis":water<.95?"is-warning":""],
      ["Жильё",`${population?Math.round(housingPopulation/population*100):0}%`,`населения размещено`,housingPopulation<population?"is-warning":""],
      ["Здоровье",`${Math.round(health*100)}%`,`среднее по области`,health<.7?"is-crisis":health<.9?"is-warning":""],
      ["Дерево",`${(timber*1000).toFixed(0)} кг`,`сырьё и топливо`,""]
    ];
    if(!cities.length){const empty=document.createElement("span");empty.className="sphere-empty";empty.textContent="В этой области нет наблюдаемых поселений.";host.append(empty);}
    for(const [label,value,note,tone] of items){if(!cities.length)break;const card=document.createElement("div");card.className=`sphere-resource ${tone}`.trim();
      const name=document.createElement("span"),number=document.createElement("strong"),small=document.createElement("small");name.textContent=label;number.textContent=value;small.textContent=note;card.append(name,number,small);host.append(card);}
    renderStocks(next,cities);renderKnowledge(cities);renderLabor(cities);
    document.getElementById("sphere-area-title").textContent=scope.label;
    document.getElementById("sphere-area-context").textContent=scope.context;
  }
  function renderStocks(next,cities){
    const host=document.getElementById("sphere-area-stocks");host.replaceChildren();
    const ids=[...new Set(cities.flatMap(city=>Object.keys(city.stocks??{})))];
    const units=next.resourceUnits??{},names=next.resourceNames??{};
    const isTonne=id=>(units[id]??"").includes("тонн"),display=(id,value)=>isTonne(id)?value*1000:value;
    const displayUnit=id=>isTonne(id)?(id==="water"?"л":"кг"):(units[id]??"ед.").replace("условный ","").replace("условное ","");
    const format=value=>value===Infinity?"∞":value>=1000?`${(value/1000).toLocaleString("ru-RU",{maximumFractionDigits:1})} тыс.`:value.toLocaleString("ru-RU",{maximumFractionDigits:value<10?1:0});
    for(const resource of aggregateResourceEstimates(cities,ids,resourceTiers,names).slice(0,14)){
      const min=display(resource.id,resource.minimum),max=display(resource.id,resource.maximum);
      const row=document.createElement("div");row.className="sphere-stock-row";
      const label=document.createElement("div"),name=document.createElement("strong"),value=document.createElement("small");label.className="sphere-stock-label";
      name.textContent=names[resource.id]??resource.id;value.textContent=resource.exact?`${format(min)} ${displayUnit(resource.id)}`:`${format(min)}–${format(max)} ${displayUnit(resource.id)}`;label.append(name,value);
      const scale=document.createElement("div"),ruler=document.createElement("div"),range=document.createElement("i"),minMarker=document.createElement("b");scale.className="sphere-stock-scale";ruler.className="sphere-stock-ruler";range.className="sphere-stock-range";minMarker.className=`sphere-stock-marker ${resource.exact?"":"is-min"}`;
      const minPosition=logPosition(min),maxPosition=logPosition(max);minMarker.style.left=`${minPosition}%`;range.style.left=`${minPosition}%`;range.style.width=`${Math.max(0,maxPosition-minPosition)}%`;
      ruler.append(range,minMarker);
      if(!resource.exact){const maxMarker=document.createElement("b");maxMarker.className=`sphere-stock-marker is-max${max===Infinity?" is-infinite":""}`;maxMarker.style.left=`${maxPosition}%`;ruler.append(maxMarker);}
      const ticks=document.createElement("div");ticks.className="sphere-stock-ticks";for(const text of ["1","10","100","1 тыс.","10 тыс."]){const tick=document.createElement("span");tick.textContent=text;ticks.append(tick);}scale.append(ruler,ticks);
      row.title=resource.exact?`${name.textContent}: точное значение ${value.textContent}`:`${name.textContent}: известный коридор ${value.textContent}`;row.append(label,scale);host.append(row);
    }
    if(!host.childElementCount){const empty=document.createElement("span");empty.className="sphere-empty";empty.textContent="Запасы не наблюдаются";host.append(empty);}
  }
  function renderKnowledge(cities){
    const host=document.getElementById("sphere-area-knowledge");host.replaceChildren();const knowledge=summarizeKnowledge(cities),total=Math.max(1,knowledge.total);
    for(const [key,label] of [["known","Известно"],["competent","Освоено"],["capable","Доступно"],["adopted","Внедрено"]]){
      const row=document.createElement("div"),track=document.createElement("span"),bar=document.createElement("i"),value=document.createElement("strong"),name=document.createElement("span");row.className="sphere-knowledge-row";track.className="sphere-knowledge-track";name.textContent=label;bar.style.width=`${Math.min(100,knowledge[key]/total*100)}%`;value.textContent=`${knowledge[key]}/${knowledge.total}`;track.append(bar);row.append(name,track,value);host.append(row);
    }
  }
  function renderLabor(cities){
    const host=document.getElementById("sphere-area-labor");host.replaceChildren();const groups=laborBreakdown(cities),total=Object.values(groups).reduce((sum,value)=>sum+value,0);
    const palette={food:"#b89b47",water:"#5f9fb8",construction:"#a77758",industry:"#8075a5",other:"#87968b",free:"#3f4c47"},labels={food:"Пища",water:"Вода",construction:"Стройка",industry:"Производства",other:"Прочее",free:"Свободно"};let cursor=0;const stops=[];
    for(const [key,value] of Object.entries(groups)){const start=total?cursor/total*100:0;cursor+=value;const end=total?cursor/total*100:0;stops.push(`${palette[key]} ${start}% ${end}%`);}
    const donut=document.createElement("div");donut.className="sphere-labor-donut";donut.style.setProperty("--labor-gradient",stops.join(","));donut.title=`Доступно и распределено ${total.toFixed(1)} чел·ч`;
    const legend=document.createElement("div");legend.className="sphere-labor-legend";
    for(const [key,value] of Object.entries(groups)){if(value<.01)continue;const row=document.createElement("div"),swatch=document.createElement("i"),label=document.createElement("span"),hours=document.createElement("strong");row.className="sphere-labor-row";row.style.setProperty("--labor-color",palette[key]);label.textContent=labels[key];hours.textContent=`${value.toFixed(1)} ч`;row.title=`${labels[key]}: ${value.toFixed(2)} человеко-часа`;row.append(swatch,label,hours);legend.append(row);}
    host.append(donut,legend);
  }
  function renderClock(next){
    document.getElementById("sphere-day").textContent=String(next.day);
    document.getElementById("sphere-calendar-date").textContent=`год ${Math.floor(next.day/360)+1} · месяц ${Math.floor(next.day%360/30)+1}`;
    const time=document.getElementById("sphere-time-progress");time.max=String(Math.max(1,next.day));time.value=String(next.day);
  }
  function render(next,{scopeOnly=false,force=false,live=false}={}) {
    if(state&&next.revision<state.revision)return;
    if(!scopeOnly&&onState(next,{live})===false)return;state=next;
    if(live){
      // Acknowledge the server after only the cheap clock update. The journal,
      // charts and detailed cards wait until the map/camera has yielded.
      renderClock(next);status.textContent=`Получен компактный поток дня ${next.day}. Подробности обновятся после кадра карты.`;controls();
      scheduleUi(()=>state&&render(state,{scopeOnly:true,force:true}));return;
    }
    const scope=getScope();
    if(scopeOnly&&!force&&scope.key===lastScopeKey)return;
    lastScopeKey=scope.key;
    const cityIds=scope.cityIds===null?null:new Set(scope.cityIds);
    const scopedCities=cityIds===null?next.cities:next.cities.filter(city=>cityIds.has(city.id));
    renderOverview(next,scopedCities,scope);
    const economy=document.getElementById("sphere-economy");
    economy.querySelector("summary").textContent=scope.level==="world"?`Поселения мира · ${scopedCities.length}`:scope.level==="region"?`Поселения в области · ${scopedCities.length}`:"Поселение: подробности";
    renderClock(next);
    document.getElementById("sphere-wildlife-status").textContent=wildlifeSummary(next.wildlife??[]);
    document.getElementById("sphere-name").textContent=next.name;
    document.getElementById("sphere-active-zones").textContent=next.activeZones.toLocaleString("ru-RU");
    const cards=document.getElementById("sphere-economy-cities");
    const expanded=new Set([...cards.querySelectorAll("details[data-panel-key][open]")].map(d=>`${d.closest("[data-city-id]").dataset.cityId}:${d.dataset.panelKey}`));
    cards.replaceChildren();
    for(const city of scopedCities) {
      const card=document.createElement("div");card.className="sphere-city-economy";
      card.dataset.cityId=city.id;
      const title=document.createElement("strong");title.textContent=city.name;
      const summary=document.createElement("p");summary.textContent=`${city.population.toLocaleString("ru-RU")} жителей · ${city.settlement?.primitive?"свежая еда":"еда"} на ${city.foodDays.toFixed(1)} дн. · здоровье ${Math.round(city.health*100)}%`;
      const life=city.settlement;
      const detail=document.createElement("p");detail.textContent=life?`Жильё: ${city.population}/${life.housingCapacity} мест · без жилья ${life.unhoused} · вода за день ${next.day?Math.round(life.waterCoverage*100)+"%":"—"}${city.shortage?" · НЕХВАТКА ЕДЫ":""}`:
        `${city.industries.length} производств · ${city.technologyCount} технологий`;
      const stocks=document.createElement("p");stocks.textContent=`Запасы: еда ${(city.stocks.food*1000).toFixed(0)} кг, вода ${((city.stocks.water??0)*1000).toFixed(0)} л, древесина ${(city.stocks.timber*1000).toFixed(0)} кг, ткань ${((city.stocks.cloth??0)*1000).toFixed(1)} кг`;
      card.append(title,summary,detail,stocks);cards.append(card);
      // Global and regional views intentionally stop at settlement summaries.
      // Household work, proposals and individual buildings only belong to a local view.
      if(life&&scope.level==="local"){
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
        if(life.processes)card.append(renderProcessPanel(city,processes,next.resourceUnits));
        if(life.storage)card.append(renderStoragePanel(city,next.resourceUnits));
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
      household_move_prepared:"Дом для постепенного переезда готов",supply_pressure:"Устойчивое ухудшение снабжения",supply_pressure_relieved:"Снабжение восстановилось",scouting_departed:"Разведчики вышли в путь",scouting_returned:"Получен отчёт разведки",scouting_casualty:"Разведка понесла потери",scouting_lost:"Разведывательная группа пропала",simulation_rules_updated:"Обновлены правила симуляции",
      decision_proposed:"Предложен проект",decision_stage_changed:"Обсуждение проекта",decision_assessed:"Оценена польза решения",settlement_building_demolished:"Постройка разобрана",lightning_fire:"Пожар от молнии",
      plant_discovered:"Найдено растение",crop_sown:"Выполнен посев",crop_harvest:"Собран урожай",crop_failed:"Посев погиб",
      animal_captured:"Живой отлов",herd_birth:"Приплод стада",herd_death:"Потеря животного",pasture_ready:"Пастбище освоено",
      resource_camp_planned:"Выбран промысловый участок",resource_camp_ready:"Промысловый лагерь готов",resource_camp_abandoned:"Промысловый лагерь заброшен"};
    const eventWindow=Number(document.getElementById("sphere-event-window").value);
    const regionalNoise=new Set(["crop_sown","crop_harvest","herd_birth","household_relocated","household_move_prepared"]);
    const worldEvents=new Set(["crisis_started","crisis_ended","lightning_fire","supply_pressure","supply_pressure_relieved","settlement_contact_sent","settlement_founded","household_discovery","decision_assessed"]);
    const filtered=next.events.filter(event=>{
      if(next.day-event.day>eventWindow)return false;
      const eventCity=event.details?.cityId??(next.cities.some(city=>city.id===event.subjectId)?event.subjectId:null);
      if(cityIds!==null&&eventCity&&!cityIds.has(eventCity))return false;
      if(scope.level==="world")return worldEvents.has(event.type);
      if(scope.level==="region"&&regionalNoise.has(event.type))return false;
      return true;
    }).slice(0,scope.level==="local"?18:10);
    const speciesLabel=id=>(biosphere&&[...biosphere.crops,...biosphere.animals].find(s=>s.id===id)?.name)??id;
    const expeditionText=event=>{
      const details=event.details??{};
      if(event.type==="scouting_departed")return `${details.people??"—"} чел. · запас ${details.provisionDays??"—"} дн. · сектор ${details.targetSector??"не указан"}`;
      if(event.type==="scouting_returned"){
        const finds=[...(details.plantIds??[]),...(details.animalIds??[])].slice(0,5).map(speciesLabel);
        const captured=Object.entries(details.capturedBySpecies??{}).map(([id,count])=>`${speciesLabel(id)} ×${count}`);
        const claims=Object.entries(details.foreignClaims??{}).map(([id,count])=>`${id}: ${count} зон`);
        return `${details.durationDays??"—"} дн. · путь ${details.routeCells??0} зон · открыто ${details.surveyedCells??0}`+
          (finds.length?` · находки: ${finds.join(", ")}`:"")+(captured.length?` · доставлены живыми: ${captured.join(", ")}`:"")+
          (claims.length?` · замечены владения: ${claims.join(", ")}`:"")+
          (details.casualties?` · потери ${details.casualties}`:"");
      }
      if(event.type==="scouting_casualty"||event.type==="scouting_lost")return `осталось ${details.remaining??0} чел. · риск ${Math.round((details.exposure??0)*100)}%`;
      return null;
    };
    for(const event of filtered){const row=document.createElement("li");
      const speciesId=event.details?.crop??event.details?.species;
      const speciesName=biosphere&&[...biosphere.crops,...biosphere.animals].find(s=>s.id===speciesId)?.name;
      const time=document.createElement("time");time.textContent=`День ${event.day}`;
      const eventName=plantDiscoveryName(event,biosphere)??eventNames[event.type]??event.type;
      const text=document.createElement("span");text.textContent=`${eventName} · ${expeditionText(event)??event.details?.name??speciesName??event.details?.reason??event.subjectId??"мир"}`;
      row.append(time,text);events.append(row);}
    if(!filtered.length){const row=document.createElement("li");row.className="empty";row.textContent="За выбранный период заметных событий нет.";events.append(row);}
    status.textContent=(next.warnings.length?next.warnings.join(". "):
      `Работает C#-симуляция на сфере. ${next.atmosphere?`Погода: ${next.atmosphere.systems} атмосферных систем; горит клеток: ${next.atmosphere.burningCells}.`:`Грузы в пути: ${next.shipments}.`}`)+" Изменения пока в памяти сервера.";
    if(next.trailSummary)document.getElementById("sphere-trail-status").textContent=
      `Тропы: ${next.trailSummary.visible} видимых участков, сегодня использовано ${next.trailSummary.usedToday}. Неиспользуемые зарастают.`;
  }
  async function syncFull() {
    try{
      const response=await fetch(`/api/sphere/simulation?${mapQuery()}`,{cache:"no-store"});
      if(!response.ok)throw new Error(`HTTP ${response.status}`);
      render(await response.json(),{force:true});controls();
    }catch(error){status.textContent=`Не удалось синхронизировать полную сводку: ${error.message}`;}
  }
  for(const button of speedButtons)button.addEventListener("click",()=>{channel.setSpeed(Number(button.dataset.speed));try{localStorage.setItem("worldgen.simulationSpeed",String(channel.speed));}catch{}controls();});
  play.addEventListener("click",()=>{channel.toggle();controls();});
  document.getElementById("sphere-event-window").addEventListener("change",()=>state&&render(state,{scopeOnly:true,force:true}));
  sites.addEventListener("change",()=>{const industry=state?.cities.flatMap(city=>city.industries).find(item=>item.id===sites.value);if(industry)onFocus(industry);});
  homes.addEventListener("change",()=>{const home=state?.cities.flatMap(city=>city.homes??[]).find(item=>item.id===homes.value);if(home)onFocus(home);});
  // A hidden embedded panel may still be the user's active simulation. Only an
  // explicit pause or actually leaving this document stops playback.
  window.addEventListener("pagehide",()=>{if(liveFrame)cancelAnimationFrame(liveFrame);channel.close();});
  try {await syncFull();channel.connect();controls();}
  catch(error){for(const button of speedButtons)button.disabled=true;play.disabled=true;status.textContent=`Не удалось загрузить симуляцию: ${error.message}`;}
  return {refreshScope(){if(state){lastScopeKey="";render(state,{scopeOnly:true});}},get state(){return state;}};
}
