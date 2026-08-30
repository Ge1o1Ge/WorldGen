const finite = value => Number.isFinite(value) ? Math.max(0, value) : Infinity;

export function logPosition(value, minimum=1, maximum=10000) {
  if (value === Infinity) return 100;
  const low = Math.log10(minimum), high = Math.log10(maximum);
  return Math.max(0, Math.min(100, (Math.log10(Math.max(minimum, value)) - low) / (high - low) * 100));
}

export function productionTiers(processes=[], activities=[]) {
  const producers=new Map();
  const add=(output,inputs)=>{if(!producers.has(output))producers.set(output,[]);producers.get(output).push(Object.keys(inputs??{}));};
  for(const process of processes)for(const output of Object.keys(process.outputs??{}))add(output,process.inputs);
  for(const activity of activities)if(activity.output)add(activity.output,activity.inputs);
  const cache=new Map(),visit=(id,path=new Set())=>{
    if(cache.has(id))return cache.get(id);if(path.has(id))return 0;
    const variants=producers.get(id);if(!variants?.length){cache.set(id,0);return 0;}
    const branch=new Set(path);branch.add(id);
    const tier=Math.max(...variants.map(inputs=>inputs.length?1+Math.max(...inputs.map(input=>visit(input,branch))):1));
    cache.set(id,tier);return tier;
  };
  for(const id of producers.keys())visit(id);
  return Object.fromEntries(cache);
}

export function aggregateResourceEstimates(cities, resourceIds, tiers={}, names={}) {
  return resourceIds.map(id => {
    let minimum = 0, maximum = 0, exact = true;
    for (const city of cities) {
      const estimate = city.stockEstimates?.[id] ?? city.stockKnowledge?.[id];
      if (estimate) {
        const min = finite(estimate.minimum ?? estimate.min ?? 0);
        const max = finite(estimate.maximum ?? estimate.max ?? Infinity);
        minimum += min; maximum = maximum === Infinity || max === Infinity ? Infinity : maximum + max;
        exact = false;
      } else {
        const value = finite(city.stocks?.[id] ?? 0);
        minimum += value; maximum = maximum === Infinity || value === Infinity ? Infinity : maximum + value;
      }
    }
    return {id, minimum, maximum, exact: exact && minimum === maximum};
  }).filter(item => item.maximum > 0).sort((a, b) =>
    (tiers[b.id]??0)-(tiers[a.id]??0) || (names[a.id]??a.id).localeCompare(names[b.id]??b.id,"ru"));
}

export function summarizeKnowledge(cities) {
  const result = {total: 0, known: 0, competent: 0, capable: 0, adopted: 0};
  for (const city of cities) for (const key of Object.keys(result)) result[key] += city.technology?.[key] ?? 0;
  return result;
}

export function laborBreakdown(cities) {
  const groups = {food: 0, water: 0, construction: 0, industry: 0, other: 0, free: 0};
  const food = new Set(["gather", "forage", "fish", "hunt", "cultivate", "garden", "harvest", "herd"]);
  const construction = new Set(["construction", "build", "repair", "demolition", "clay", "stone"]);
  for (const city of cities) {
    const life = city.settlement; if (!life) continue;
    let classified = 0;
    for (const task of life.tasks ?? []) {
      const hours = Math.max(0, task.hours ?? 0); classified += hours;
      if (task.activity === "water") groups.water += hours;
      else if (food.has(task.activity)) groups.food += hours;
      else if (construction.has(task.activity)) groups.construction += hours;
      else groups.other += hours;
    }
    const industry = Math.max(0, life.industryLaborHours ?? 0); groups.industry += industry;
    const used = Math.max(0, life.laborUsedHours ?? 0) + industry;
    groups.other += Math.max(0, used - classified - industry);
    groups.free += Math.max(0, (life.laborAvailableHours ?? 0) - used);
  }
  return groups;
}
