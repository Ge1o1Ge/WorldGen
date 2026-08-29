const names={food:"свежая еда",winter_food:"зимние запасы",water:"вода",timber:"древесина",firewood:"топливо",fiber:"волокно",cloth:"ткань"};
const volume=n=>`${n.toFixed(2)} усл. м³`;
export function storageSummary(state){
  if(!state)return "Хранение: расчёт ещё не выполнялся";
  return `Хранение: ${volume(state.usedVolume)}/${volume(state.totalCapacity)} · снаружи ${volume(state.outdoorVolume)}`;
}
function amounts(values,units){return Object.entries(values??{}).filter(([,n])=>n>1e-8).sort((a,b)=>b[1]-a[1]).map(([id,n])=>{
  const unit=units?.[id];return `${names[id]??id} ${unit==="комплект"?`${n.toFixed(2)} компл.`:unit?.includes("литр")||id==="water"?`${(n*1000).toFixed(1)} л`:`${(n*1000).toFixed(1)} кг`}`;
}).join(" · ")||"нет";}
export function renderStoragePanel(city,units){
  const state=city.settlement.storage,box=document.createElement("details"),title=document.createElement("summary");
  box.dataset.panelKey="storage";title.textContent=storageSummary(state);box.append(title);
  const stored=document.createElement("p");stored.textContent=`Под крышей: ${amounts(state?.storedByResource,units)}.`;
  const outside=document.createElement("p");outside.textContent=`Под открытым небом: ${amounts(state?.outdoorByResource,units)}.`;
  const loss=document.createElement("p");loss.textContent=`Испорчено за день: ${amounts(state?.lostByResource,units)}.`;
  const explanation=document.createElement("p");explanation.textContent="Запасы автоматически занимают наиболее подходящие свободные помещения. Износ здания уменьшает полезную вместимость; наружный остаток портится быстрее.";
  box.append(stored,outside,loss,explanation);return box;
}
