export function habitatSuitability(h,temperature,moisture,forest){
  if(temperature<h.minTemperature||temperature>h.maxTemperature||moisture<h.minMoisture||moisture>h.maxMoisture||forest<h.minForest||forest>h.maxForest)return 0;
  const clamp=v=>Math.max(.1,Math.min(1,v));
  return clamp(Math.min((temperature-h.minTemperature+2)/5,(h.maxTemperature-temperature+2)/5))*clamp(Math.min((moisture-h.minMoisture+.08)/.2,(h.maxMoisture-moisture+.08)/.2));
}
export function speciesPresence(id,seed,p){
  let hash=(2166136261^seed)>>>0;for(const c of id)hash=Math.imul(hash^c.charCodeAt(0),16777619)>>>0;
  const phase=(hash%10000)/1000;
  return Math.max(0,Math.min(1,(Math.sin(p.x*7+phase)+Math.cos(p.z*6+phase*2)+Math.sin(p.y*5-phase)+.65)/2.6));
}
export function wildPlants(catalog,seed,point,sample){
  if(!catalog||!Number.isFinite(sample.temperature))return [];
  return catalog.crops.map(c=>({crop:c,score:speciesPresence(c.id,seed,point)*habitatSuitability(c.habitat,sample.temperature,sample.moisture,sample.forest)}))
    .filter(c=>c.score>.22).sort((a,b)=>b.score-a.score).map(p=>p.crop);
}
export function biologicalGlyph(kind,plants,variant){
  if(variant<.45||!plants.length)return kind;
  return plants[Math.min(plants.length-1,Math.floor((variant-.45)/.55*plants.length))].symbol;
}
export function biologyLines(city,catalog){
  if(!city.biology||!catalog)return [];
  const b=city.biology,names=new Map([...catalog.crops,...catalog.animals].map(s=>[s.id,s.name]));
  const seeds=catalog.crops.filter(c=>(city.stocks['seed_'+c.id]??0)>.000001).map(c=>`${c.name}: ${(city.stocks['seed_'+c.id]*1000).toFixed(2)} кг`);
  return [
    `Найдены растения: ${b.knownPlants.map(id=>names.get(id)??id).join(', ')||'ещё не обследованы'}.`,
    `Посадочный материал: ${seeds.join('; ')||'нет'}.`,
    `Получен урожай: ${b.harvestedCrops.map(id=>names.get(id)??id).join(', ')||'пока нет'}; суммарно ${(b.harvestedTonnes*1000).toFixed(0)} кг.`,
    ...Object.entries(b.herds).filter(([,h])=>h.count>0||h.captured>0).map(([id,h])=>`${names.get(id)} · самки: ${h.females}, самцы: ${h.males}, молодняк: ${h.young.reduce((s,y)=>s+y.count,0)}; захвачено ${h.captured}, родилось ${h.births}, погибло ${h.deaths}, забой ${h.slaughtered??0}${h.pastureWork>=24?' · пастбище освоено':''}.`),
    `Промысловые лагеря: ${b.camps.filter(c=>!c.abandoned).length}; доставлено древесины ${(b.campTimberDelivered*1000).toFixed(0)} кг.`,
    ...Object.entries(b.plots).filter(([id,p])=>p.cropId&&(!city.activeCropPlots||city.activeCropPlots.includes(id))).slice(0,20).map(([id,p])=>`${names.get(p.cropId)} · ${(p.area*100).toFixed(0)}% поля · ${p.phase} · ${Math.round(p.degreeDays)} градусо-дней`)
  ];
}
 function renderLines(lines){const box=document.createElement('details');box.dataset.panelKey='biology';const title=document.createElement('summary');title.textContent='Растения, семена, стада и промысловые лагеря';box.append(title);for(const line of lines){const p=document.createElement('p');p.textContent=line;box.append(p);}return box;}
export function renderBiologyPanel(city,catalog){return renderLines(biologyLines(city,catalog));}
