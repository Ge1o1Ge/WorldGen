import {readFileSync} from 'node:fs';

// Read-only summary of CLI --report output; does not advance or alter a world.
for (const path of process.argv.slice(2)) {
  const run = JSON.parse(readFileSync(path, 'utf8'));
  const last = run.reports.at(-1);
  console.log(JSON.stringify({path, seed:run.seed, day:run.day, labor:run.maximumLaborShare,
    activeZones:run.activeZones, fires:run.fires, hash:run.stateHash,
    population:last.cities.reduce((sum,c)=>sum+c.population,0)}));
  for (const city of last.cities) {
    const b=city.biology;
    console.log(JSON.stringify({id:city.Id,population:city.population,shortageDays:city.shortageDays,
      knownPlants:b.KnownPlants.length,crops:b.HarvestedCrops,harvestTonnes:b.HarvestedTonnes,
      activeFields:city.fields.filter(f=>f.Status==='active').length,
      herds:Object.entries(b.Herds).filter(([,h])=>h.Count>0).map(([id,h])=>({id,count:h.Count,
        captured:h.Captured,births:h.Births,slaughtered:h.Slaughtered,deaths:h.Deaths})),
      camps:b.Camps.length,winterFood:city.winterFood,rotation:city.known.includes('crop_rotation')}));
  }
}
