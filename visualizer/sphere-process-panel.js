const fallbackNames={pottery_ware:"посуда",cheese:"сыр",garments:"одежда",charcoal:"древесный уголь"};

function resourceAmount(id,value,units){
  const unit=units?.[id];
  if(unit==="комплект")return `${value.toFixed(value<.01?3:2)} компл.`;
  if(unit?.includes("литр")||id==="water")return `${(value*1000).toFixed(1)} л`;
  return `${(value*1000).toFixed(value<.01?2:1)} кг`;
}

function constraintText(value){
  if(!value)return "цель достигнута";
  const [kind,id]=value.split(":");
  return ({technology:"нет знания",equipment:"нет оснащения",input:"нет сырья",labor:"нет свободного труда"})[kind]+(id?`: ${fallbackNames[id]??id}`:"");
}

export function processSummary(states){
  const values=Object.values(states??{});
  if(!values.length)return "Ремесленные процессы: ещё не запускались";
  const active=values.filter(state=>state.batchesToday>1e-9).length;
  const labor=values.reduce((sum,state)=>sum+(state.laborHoursToday??0),0);
  return `Ремесленные процессы: ${active} активных · ${labor.toFixed(labor>0&&labor<.1?2:1)} чел·ч сегодня`;
}

export function processLines(states,catalog,units){
  const rules=new Map((catalog??[]).map(rule=>[rule.id,rule]));
  return Object.entries(states??{}).sort(([a],[b])=>a.localeCompare(b,"ru")).map(([id,state])=>{
    const rule=rules.get(id),outputs=Object.entries(rule?.outputs??{}).map(([resource,value])=>
      `${fallbackNames[resource]??resource} ${resourceAmount(resource,value*(state.batchesToday??0),units)}`).join(" · ");
    return `${rule?.name??id}: ${(state.batchesToday??0).toFixed(3)} партий сегодня${outputs?` (${outputs})`:""}; всего ${(state.totalBatches??0).toFixed(2)} · ${constraintText(state.constraint)}.`;
  });
}

export function renderProcessPanel(city,catalog,units){
  const states=city.settlement.processes,box=document.createElement("details"),title=document.createElement("summary");
  box.dataset.panelKey="processes";title.textContent=processSummary(states);box.append(title);
  for(const text of processLines(states,catalog,units)){const row=document.createElement("p");row.textContent=text;box.append(row);}
  const note=document.createElement("p");note.textContent="Процессы используют общий труд поселения, расходуют реальные входные запасы, требуют оснащения и останавливаются после достижения целевого запаса.";box.append(note);
  return box;
}
