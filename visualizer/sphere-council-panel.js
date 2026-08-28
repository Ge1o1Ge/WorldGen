const phases={idea:"Поддержка идеи",site:"Спор о месте",approved:"Одобрено · ожидает исполнения",
  executing:"Строительство",observing:"Наблюдаем использование",assessed:"Результат оценён",
  uncertain:"Результат неоднозначен · без штрафа",cancelled:"Идея снята · без штрафа"};
export function proposalSummary(p){
  const stage=phases[p.phase]??p.phase;
  if(p.phase==="idea")return `${stage}: ${p.support.toFixed(1)} / ${p.requiredSupport.toFixed(1)} очков. Одного порога недостаточно: нужен текущий кворум.`;
  if(p.phase==="site")return `${stage}: требуется ${p.requiredSiteSupport.toFixed(1)} очков, кворум и устойчивое большинство. Перевес держится ${p.leadingDays} дн.`;
  if(p.phase==="observing")return `${stage}: ${p.observedDays} дн. наблюдений без кризиса; награда пока не выдана.`;
  if(p.phase==="assessed")return `${stage}: ${p.outcome>0?"есть польза":p.outcome<0?"польза не подтверждена":"нейтральный результат"}, день ${p.assessedDay}.`;
  return stage;
}
export function renderCouncilPanel({council,onFocus}){
  const section=document.createElement("section");section.className="sphere-supply sphere-council";
  const title=document.createElement("strong");title.textContent="Совет поселения";
  const budget=document.createElement("p");budget.textContent=council.lastDay<0?"Первое обсуждение — на следующем шаге.":
    `${council.households} домохозяйств · участие сегодня ${council.spentToday.toFixed(0)}/${council.issuedToday.toFixed(0)}. `+
    `Вес с учётом практики и репутации: ${council.weightedToday.toFixed(1)}.`;
  section.append(title,budget);
  const reputation=document.createElement("p"),domains={construction:"строительство",water:"вода",food:"пища"};
  reputation.textContent="Доверие: "+Object.entries(council.reputation).map(([id,r])=>`${domains[id]??id} ${r.minimum.toFixed(2)}–${r.maximum.toFixed(2)}`).join(" · ");section.append(reputation);
  const pending=council.proposals.filter(p=>!["assessed","uncertain","cancelled"].includes(p.phase));
  const recent=council.proposals.filter(p=>["assessed","uncertain","cancelled"].includes(p.phase)).slice(-2);
  if(!pending.length){const empty=document.createElement("p");empty.textContent="Активных предложений нет. Неиспользованное дневное участие не копится.";section.append(empty);}
  for(const proposal of [...pending,...recent]){
    const item=document.createElement("div");item.className="sphere-council-proposal";
    const name=document.createElement("p");name.textContent=`${proposal.reason} · предложено в день ${proposal.createdDay}`;
    const status=document.createElement("p");status.textContent=proposalSummary(proposal);item.append(name,status);
    if(proposal.outcomeNote){const note=document.createElement("p");note.textContent=proposal.outcomeNote;item.append(note);}
    for(const site of proposal.sites.filter(s=>proposal.phase==="site"||s.id===proposal.selectedSite)){
      const button=document.createElement("button");button.type="button";
      button.textContent=`Посмотреть ${site.x}:${site.y} · ${site.support.toFixed(1)} очков${site.id===proposal.selectedSite?" · выбрано":""}${!site.available&&proposal.phase==="site"?" · недоступно":""}`;
      button.title="Только просмотр участка: это не голос наблюдателя и не приказ строить.";
      button.addEventListener("click",()=>onFocus(site));item.append(button);
    }
    section.append(item);
  }
  return section;
}
