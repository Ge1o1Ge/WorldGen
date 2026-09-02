export const structuredJournalEvents=new Set([
  "crop_sown","crop_harvest","crop_failed","crop_loss",
  "animal_captured","herd_birth","herd_death","pasture_ready","pasture_moved"
]);

const youngCount=herd=>(herd.young??[]).reduce((sum,group)=>sum+(group.count??0),0);

export function aggregateHerds(cities=[]){
  const rows=new Map();
  for(const city of cities)for(const [species,herd] of Object.entries(city.biology?.herds??{})){
    const young=youngCount(herd),count=herd.count??(herd.females??0)+(herd.males??0)+young;
    if(count<=0&&(herd.captured??0)<=0&&(herd.births??0)<=0&&(herd.deaths??0)<=0)continue;
    const row=rows.get(species)??{species,settlements:0,count:0,females:0,males:0,young:0,captured:0,births:0,deaths:0,slaughtered:0,
      activePastures:0,preparingPastures:0,healthTotal:0,healthWeight:0,products:{}};
    row.settlements++;row.count+=count;row.females+=herd.females??0;row.males+=herd.males??0;row.young+=young;
    row.captured+=herd.captured??0;row.births+=herd.births??0;row.deaths+=herd.deaths??0;row.slaughtered+=herd.slaughtered??0;
    if(herd.pasture){if((herd.pastureWork??0)>=24)row.activePastures++;else row.preparingPastures++;}
    row.healthTotal+=(herd.health??1)*Math.max(1,count);row.healthWeight+=Math.max(1,count);
    for(const [resource,amount] of Object.entries(herd.totalProducts??{}))row.products[resource]=(row.products[resource]??0)+amount;
    rows.set(species,row);
  }
  return [...rows.values()].map(row=>({...row,health:row.healthWeight?row.healthTotal/row.healthWeight:0}))
    .sort((a,b)=>b.count-a.count||b.captured-a.captured||a.species.localeCompare(b.species));
}

export function aggregateScouting(cities=[],activeScouts=[]){
  const cityNames=new Map(cities.map(city=>[city.id,city.name]));
  const herds=new Map(cities.map(city=>[city.id,city.biology?.herds??{}]));
  const reports=cities.flatMap(city=>(city.scoutReports??[]).map(report=>{
    const captured=Object.entries(report.capturedAnimals??{});
    return {...report,cityId:city.id,cityName:city.name,captured,
      established:captured.filter(([species])=>(herds.get(city.id)?.[species]?.count??0)>0).map(([species])=>species),
      foundPlants:[...new Set(report.plants??[])],foundAnimals:[...new Set(report.animals??[])]};
  })).sort((a,b)=>(b.receivedDay??0)-(a.receivedDay??0));
  const active=activeScouts.filter(scout=>cityNames.has(scout.cityId)).map(scout=>({...scout,cityName:cityNames.get(scout.cityId)}));
  return {
    reports,active,
    totals:{journeys:reports.length,surveyed:reports.reduce((sum,report)=>sum+(report.surveyedCells??0),0),
      routeCells:reports.reduce((sum,report)=>sum+(report.routeCells??0),0),
      captured:reports.reduce((sum,report)=>sum+report.captured.reduce((n,[,count])=>n+count,0),0),
      casualties:reports.reduce((sum,report)=>sum+(report.casualties??0),0)}
  };
}

const appendCells=(row,values)=>{for(const value of values){const cell=document.createElement("td");cell.textContent=value;row.append(cell);}};

export function renderHerdReport(cities,scope,biosphere){
  const host=document.getElementById("sphere-herd-history"),rows=aggregateHerds(cities);host.replaceChildren();
  host.hidden=!rows.length||scope.level==="world";if(host.hidden)return;
  const title=document.createElement("h4");title.textContent="Животноводство · накопленная история";
  const table=document.createElement("table");table.className="sphere-crop-table sphere-herd-table";
  table.innerHTML="<thead><tr><th>Вид</th><th>гол.</th><th>♀/♂/мол.</th><th>отлов</th><th>рожд./пот.</th><th>выпас</th><th>сост.</th></tr></thead>";
  const body=document.createElement("tbody"),name=id=>biosphere?.animals?.find(animal=>animal.id===id)?.name??id;
  for(const herd of rows){
    const row=document.createElement("tr");
    const pasture=herd.activePastures?`${herd.activePastures} акт.${herd.preparingPastures?` +${herd.preparingPastures}`:""}`:herd.preparingPastures?`${herd.preparingPastures} готов.`:"—";
    const condition=herd.count===0&&herd.captured?"не закрепилось":herd.count===1?"нет пары":`${Math.round(herd.health*100)}%`;
    appendCells(row,[name(herd.species),String(herd.count),`${herd.females}/${herd.males}/${herd.young}`,String(herd.captured),`${herd.births}/${herd.deaths+herd.slaughtered}`,pasture,condition]);
    const products=Object.entries(herd.products).filter(([,amount])=>amount>0).map(([id,amount])=>`${id}: ${amount.toFixed(2)}`).join(" · ");
    row.title=`${name(herd.species)} · ${herd.settlements} пос. · здоровье ${Math.round(herd.health*100)}%${products?` · продукция: ${products}`:""}`;body.append(row);
  }
  table.append(body);host.append(title,table);
}

export function renderScoutReport(cities,scope,activeScouts,biosphere){
  const host=document.getElementById("sphere-scout-history"),summary=aggregateScouting(cities,activeScouts);host.replaceChildren();
  host.hidden=scope.level==="world"||(!summary.reports.length&&!summary.active.length);if(host.hidden)return;
  const speciesName=id=>[...(biosphere?.crops??[]),...(biosphere?.animals??[])].find(species=>species.id===id)?.name??id;
  const title=document.createElement("h4");title.textContent="Разведка · последние отчёты";
  const metrics=document.createElement("div");metrics.className="sphere-scout-metrics";
  for(const [label,value] of [["в пути",summary.active.length],["походов",summary.totals.journeys],["новых зон",summary.totals.surveyed],["живыми",summary.totals.captured]]){
    const item=document.createElement("span"),number=document.createElement("strong");number.textContent=String(value);item.append(number,` ${label}`);metrics.append(item);
  }
  host.append(title,metrics);
  if(summary.active.length){const active=document.createElement("div");active.className="sphere-scout-active";
    for(const scout of summary.active){const row=document.createElement("p"),cargo=Object.entries(scout.capturedAnimals??{}).map(([id,count])=>`${speciesName(id)} ×${count}`).join(", ");
      row.textContent=`${scout.cityName}: ${scout.phase==="returning"?"возвращаются":"в пути"} · ${scout.currentInterest??"держат курс"} · ${scout.traversedCells??0} зон${cargo?` · живой груз: ${cargo}`:""}`;active.append(row);}host.append(active);}
  if(!summary.reports.length)return;
  const table=document.createElement("table");table.className="sphere-crop-table sphere-scout-table";
  table.innerHTML="<thead><tr><th>День</th><th>Поселение</th><th>дн./путь</th><th>новое</th><th>находки</th><th>живой груз</th></tr></thead>";
  const body=document.createElement("tbody");
  for(const report of summary.reports.slice(0,8)){
    const row=document.createElement("tr"),finds=[...report.foundPlants,...report.foundAnimals].map(speciesName);
    const cargo=report.captured.map(([id,count])=>`${speciesName(id)} ×${count}${report.established.includes(id)?" ✓":" · не закреп."}`).join(", ")||"—";
    appendCells(row,[String(report.receivedDay??"—"),report.cityName,`${report.durationDays??0}/${report.routeCells??0}`,String(report.surveyedCells??0),finds.length?`${finds.length}: ${finds.slice(0,2).join(", ")}${finds.length>2?"…":""}`:"—",cargo]);
    row.title=report.outcome??"";body.append(row);
  }
  table.append(body);host.append(table);
}
