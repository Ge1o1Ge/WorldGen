import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {speciesPresence,wildPlants,biologicalGlyph,biologyLines} from '../visualizer/sphere-biology.js';
import {worldTileSymbols} from '../visualizer/sphere-world-geometry.js';
const catalog=JSON.parse(readFileSync(new URL('../content/worlds/biosphere.json',import.meta.url)));
test('every biological species references an existing vector symbol and stable unique ID',()=>{
 const atlas=JSON.parse(readFileSync(new URL('../visualizer/assets/topographic-symbols.json',import.meta.url)));
 const ids=new Set(atlas.symbols.map(s=>s.id));
 for(const s of [...catalog.crops,...catalog.animals])assert.ok(ids.has(s.symbol),s.symbol);
 assert.equal(new Set([...catalog.crops,...catalog.animals].map(s=>s.id)).size,32);
});
test('species ranges are finite and continuous through a cube seam; dry/cold habitats reject rice',()=>{
 const a={x:Math.SQRT1_2,y:0,z:Math.SQRT1_2},b={...a,x:a.x+1e-9};
 for(const s of catalog.crops){assert.ok(Math.abs(speciesPresence(s.id,271828,a)-speciesPresence(s.id,271828,b))<1e-7);}
 assert.ok(!wildPlants(catalog,271828,a,{temperature:0,moisture:.1,forest:.5}).some(c=>c.id==='rice'));
 assert.deepEqual(wildPlants(catalog,271828,a,{}),[]);
});
test('biological glyph changes preserve world anchors and do not depend on camera',()=>{
 const options={face:'PositiveX',tx:0,ty:0,size:416,tileSize:32,zoom:16,seed:271828,settlements:[],seaLevel:0,
   sample:()=>({elevation:100,lakeDepth:0,temperature:16,moisture:.55,forest:.7,biome:2}),biosphere:catalog};
 assert.deepEqual(worldTileSymbols(options),worldTileSymbols({...options,camera:{yaw:1,pitch:.3}}));
 assert.equal(biologicalGlyph('deciduous',[{symbol:'fruit_tree'}],.9),'fruit_tree');
 assert.equal(biologicalGlyph('deciduous',[],.9),'deciduous');
});
test('settlement biology distinguishes found seeds from harvested crops and does not invent herds',()=>{
 const city={stocks:{seed_wheat:.001},biology:{knownPlants:['wheat'],harvestedCrops:[],harvestedTonnes:0,herds:{},plots:{},camps:[],campTimberDelivered:0}};
 const lines=biologyLines(city,catalog).join('\n');assert.match(lines,/Пшеница: 1.00 кг/);assert.match(lines,/Получен урожай: пока нет/);
 assert.doesNotMatch(lines,/самок/);assert.deepEqual(biologyLines({},catalog),[]);
});
