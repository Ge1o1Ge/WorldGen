import { dampZoom, SphereCamera, MIN_SPHERE_ZOOM, MAX_SPHERE_ZOOM } from "/sphere-camera.js";
import { SphereChunkCache } from "/sphere-chunks.js";
import { facePoint as surfacePoint, locateFace, createSurfaceSampler, contourInterval } from "/sphere-cartography.js";
import { createSymbolRenderer, symbolSvg } from "/map-symbols.js";
import { drawCartographicLayer } from "/sphere-map-layer.js";
import { buildingNames,buildingStates } from "/settlement-symbols.js";
import {buildingConditionText,buildingHoverText} from "/sphere-lifecycle-panel.js";
import { roundSpherePath } from "/sphere-world-geometry.js";
import { connectSphereSimulation } from "/sphere-simulation-panel.js?v=batched-playback1";
import { SphereMapData,structureTiles } from "/sphere-map-data.js";
import {createLakeSurfaceSampler} from "/sphere-water.js";
import {paintWaterRaster} from "/sphere-water-raster.js";
import {createWeatherSampler,createClimateSampler,climateSeries,WeatherTint,WeatherEffects,weatherEffectSites,weatherText} from '/sphere-weather.js?v=climate-history1';
import {wildPlants} from '/sphere-biology.js';
import {WebGlobeRenderer} from '/sphere-webgl.js';
import {MobileUnitLayer} from '/sphere-mobile-units.js';

const canvas = document.getElementById("sphere-map");
const context = canvas.getContext("2d", { alpha: true });
const globeRenderer=new WebGlobeRenderer(document.getElementById("sphere-globe"));
const mobileUnits=new MobileUnitLayer(document.getElementById("sphere-mobile-units"),{
  pointForCell:cell=>facePoint(cell.face,cell.x,cell.y),
  project:point=>{const view=rotateWorldToView(point),geometry=globeGeometry();return{x:(geometry.centerX+view.x*geometry.radius)/pixelRatio,y:(geometry.centerY-view.y*geometry.radius)/pixelRatio,z:view.z};}
});
const weatherTint=new WeatherTint(()=>document.createElement('canvas'));
let weatherInView=true;
const weatherEffects=new WeatherEffects(document.getElementById('sphere-weather-effects'),{hidden:()=>document.hidden||!weatherInView});
const weatherLayer=document.getElementById('sphere-weather-layer');
const weatherMotion=document.getElementById('sphere-weather-motion');
let sampleWeather=null;
let sampleClimate=null;
let currentClimateSeries=[];
let climateField="temperature";
const landInk=document.createElement("canvas"),mapInk=document.createElement("canvas"),waterMask=document.createElement("canvas"),interactiveInk=document.createElement("canvas");
const layerSelect = document.getElementById("sphere-layer");
const tooltip = document.getElementById("sphere-tooltip");
const selection = document.getElementById("sphere-selection");
const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
const background = dark ? [25, 32, 30] : [255, 253, 247];
const biomeColors = dark
  ? [[43, 88, 106], [92, 101, 92], [107, 91, 56], [91, 102, 67], [47, 85, 56], [55, 91, 78], [102, 94, 78]]
  : [[83, 151, 177], [184, 194, 181], [205, 179, 116], [174, 190, 124], [92, 139, 91], [112, 159, 139], [156, 145, 125]];
const biomeNames = ["океан", "тундра", "сухая степь", "луг", "лес", "болото", "высокогорье"];
const settlementColors = dark
  ? [[61, 151, 112], [183, 132, 70], [91, 138, 181], [164, 96, 137]]
  : [[92, 166, 119], [201, 146, 77], [91, 148, 193], [184, 111, 151]];
const faceLabels = {
  PositiveX: "+X", NegativeX: "−X", PositiveY: "+Y",
  NegativeY: "−Y", PositiveZ: "+Z", NegativeZ: "−Z"
};

let metadata;
let preview;
let faces;
const camera = new SphereCamera();
let pixelRatio = 1;
let drag = null;
let frame = null;
let cameraInteraction = false;
let cameraSettleTimer = null;
let chunkRenderTimer = null;
let zoomMotion = null;
let tooltipTimer = null;
let lastHover = null;
let neededChunks = new Set();
let selectedCell = null;
let chunkAxis;
let faceIds;
let refreshPromise = null;
let simulationView=null;
let simulationExactPending=false;
let simulationController=null;
let interfaceWorkTimer=null;
let interfaceIdle=0;
const interfaceWork=new Map();
let mapData;
let chunkRequests=0;
let rasterSnapshot=null;
let rasterBuilds=0;
let simulationReconcileTimer=null;
let parcels=[];
let lastInteractiveStatusAt=0;

function refreshParcelOptions(){
  const select=document.getElementById("sphere-parcel");if(!metadata||!select)return;
  const previous=select.value!==""?parcels[Number(select.value)]?.id:null;parcels=[];select.replaceChildren();
  const empty=document.createElement("option");empty.value="";empty.textContent="Выбрать…";select.append(empty);
  for(const settlement of metadata.settlements){const group=document.createElement("optgroup");group.label=settlement.name;
    for(const parcel of [...settlement.buildings,...settlement.usedLands]){const option=document.createElement("option");option.value=String(parcels.length);
      option.textContent=`${buildingNames[parcel.buildingTypeId]??parcel.id} · ${parcel.x}:${parcel.y}${parcel.slot>=0?` · место ${parcel.slot+1}`:""}`;
      if(parcel.id===previous)option.selected=true;parcels.push(parcel);group.append(option);}select.append(group);}
}
let atlas;
let drawSymbol;
let hydrology = null;
let sampleLakeSurface;
const mapOptions = { contours:true, symbols:true, water:true, boundaries:true, wildlife:true, winter:true };
const lastLandUsage = new Map();
const chunkCache = new SphereChunkCache({
  fetchChunk: async (key, signal) => {
    chunkRequests++;
    const face = metadata.faces[Math.floor(key / (chunkAxis * chunkAxis))];
    const local = key % (chunkAxis * chunkAxis);
    const response = await fetch(`/api/sphere/chunks/${face}/${local % chunkAxis}/${Math.floor(local / chunkAxis)}`, { signal, cache:"no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const chunk = await response.json();
    if (chunk.worldId !== metadata.worldId) throw new Error("Мир на сервере заменён. Обновите страницу.");
    globeRenderer.updateChunk(chunk);
    return chunk;
  },
  onChange: () => {
    // Chunk arrival must not inject expensive map frames into an active drag.
    // The current GPU snapshot is complete enough for motion; one batched sharp
    // frame reconciles the cache after the camera settles.
    if(cameraInteraction||drag)return;
    clearTimeout(chunkRenderTimer);chunkRenderTimer=setTimeout(()=>{
      updateViewStatus();refreshSelection();scheduleRender();
    },120);
  }
});

function chunkTile(key){
  const perFace=chunkAxis*chunkAxis,faceIndex=Math.floor(key/perFace),local=key%perFace;
  return {face:metadata.faces[faceIndex],tx:local%chunkAxis,ty:Math.floor(local/chunkAxis)};
}

function chunkPrefetchBelt(required){
  // A breadth-first halo is spatially stable across cube seams because
  // dependenciesFor projects every neighbour through the sphere topology.
  const seen=new Set(required),prefetch=[],room=Math.max(0,chunkCache.capacity-seen.size);
  if(!room)return prefetch;
  let frontier=[...required];
  const rings=required.size<=36?3:required.size<=96?2:1;
  for(let ring=0;ring<rings&&frontier.length&&prefetch.length<room;ring++){
    const next=[];
    for(const key of frontier){
      for(const neighbour of mapData.dependenciesFor(chunkTile(key))){
        if(seen.has(neighbour))continue;
        seen.add(neighbour);prefetch.push(neighbour);next.push(neighbour);
        if(prefetch.length>=room)break;
      }
      if(prefetch.length>=room)break;
    }
    frontier=next;
  }
  return prefetch;
}

function setChunkWorkingSet(required){
  chunkCache.setDesired(required,chunkPrefetchBelt(required));
}

function clamp(value, minimum, maximum) {
  return Math.max(minimum, Math.min(maximum, value));
}

function rotateViewToWorld(x, y, z) {
  return camera.toWorld(x, y, z);
}

function sampleAt(location) {
  const cellX = clamp(Math.floor((location.u + 1) * 0.5 * metadata.faceSize), 0, metadata.faceSize - 1);
  const cellY = clamp(Math.floor((location.v + 1) * 0.5 * metadata.faceSize), 0, metadata.faceSize - 1);
  if (camera.zoom >= 3.5) {
    const chunkX = Math.floor(cellX / metadata.chunkSize);
    const chunkY = Math.floor(cellY / metadata.chunkSize);
    const key = faceIds.get(location.face) * chunkAxis * chunkAxis + chunkY * chunkAxis + chunkX;
    neededChunks.add(key);
    const chunk = chunkCache.get(key);
    if (chunk) {
      const index = (cellY - chunk.originY) * chunk.width + cellX - chunk.originX;
      return mapData.read({face:location.face,x:cellX,y:cellY},{ elevation: chunk.elevationMeters[index], temperature:chunk.temperatureC?.[index],moisture: chunk.moisture[index],
        forest: chunk.forestCover[index], biome: chunk.biome[index],
        owner: chunk.owner[index], influence: chunk.influence[index], exact: true, cellX, cellY });
    }
  }
  const x = clamp(Math.floor((location.u + 1) * 0.5 * preview.resolution), 0, preview.resolution - 1);
  const y = clamp(Math.floor((location.v + 1) * 0.5 * preview.resolution), 0, preview.resolution - 1);
  const face = faces.get(location.face);
  const index = y * preview.resolution + x;
  return mapData.read({face:location.face,x:Math.min(metadata.faceSize-1,x*preview.stride+Math.floor(preview.stride/2)),
    y:Math.min(metadata.faceSize-1,y*preview.stride+Math.floor(preview.stride/2))},{
    elevation: face.elevation[index],
    temperature: face.temperature?.[index],
    moisture: face.moisture[index],
    forest: face.forest[index],
    biome: face.biome[index],
    owner: face.owner[index],
    influence: face.influence[index],
    previewX: x,
    previewY: y, exact: false, cellX, cellY
  });
}

function tileVersion(tile,dynamic=false) {
  const versions=[metadata.worldId,hydrologyVersion(),camera.zoom>=3.5];
  for(const key of mapData.dependenciesFor(tile)){
    if(camera.zoom>=3.5)versions.push(chunkCache.get(key)?1:0);
    if(dynamic)versions.push(mapData.version(key));
  }
  return versions.join(":");
}

function hydrologyVersion(){return hydrology?`${hydrology.generator}:${hydrology.revision??0}`:"pending";}

function mix(left, right, amount) {
  return [
    left[0] + (right[0] - left[0]) * amount,
    left[1] + (right[1] - left[1]) * amount,
    left[2] + (right[2] - left[2]) * amount
  ];
}

function terrainColor(sample, point) {
  const layer = layerSelect.value;
  let color;
  if (layer === "topographic") {
    // Paper tint carries continuous terrain signals, never square biome blocks.
    const forest = clamp((sample.forest - .30) / .25, 0, 1);
    let r=244-40*forest,g=240-20*forest,b=223-36*forest;
    if(sample.biome === 1) {r+=(229-r)*.55;g+=(231-g)*.55;b+=(224-b)*.55;}
    if(sample.biome === 2) {r+=(238-r)*.3;g+=(223-g)*.3;b+=(184-b)*.3;}
    if(sample.biome === 5) {r+=(200-r)*.45;g+=(220-g)*.45;b+=(211-b)*.45;}
    // Water colour and its outline are composited together after sampling.
    // Retain just enough spherical shading at overview scale, not a day/night filter.
    const lighting = camera.zoom < 2 ? .93 + Math.max(0,point.z*.7+point.y*.3)*.07 : 1;
    return [Math.round(r*lighting),Math.round(g*lighting),Math.round(b*lighting)];
  } else if (layer === "elevation") {
    const normalized = clamp((sample.elevation + 260) / 1000, 0, 1);
    color = mix([46, 96, 121], [226, 217, 183], normalized);
  } else if (layer === "moisture") {
    color = mix([164, 126, 77], [54, 127, 156], sample.moisture);
  } else if (layer === "forest") {
    color = sample.biome === 0 ? biomeColors[0] : mix([205, 190, 135], [35, 109, 55], sample.forest);
  } else if (layer === "political") {
    color = sample.owner >= 0
      ? mix(settlementColors[sample.owner % settlementColors.length], [235, 225, 199], 0.14)
      : mix(biomeColors[sample.biome], background, 0.62);
  } else {
    color = biomeColors[sample.biome];
  }
  const lightDirection = { x: -0.42, y: 0.58, z: 0.69 };
  const diffuse = clamp(point.x * lightDirection.x + point.y * lightDirection.y + point.z * lightDirection.z, -1, 1);
  const lighting = 0.78 + Math.max(0, diffuse) * 0.22;
  return color.map(channel => clamp(Math.round(channel * lighting), 0, 255));
}

function lakeDepthAt(location) {
  return sampleLakeSurface?.(location) ?? 0;
}

function smoothSampler() {
  const exact = camera.zoom >= 3.5;
  const sampler=createSurfaceSampler({size:metadata.faceSize,stride:exact?1:preview.stride,
    origin:exact?0:Math.floor(preview.stride/2), read:(face,x,y)=>sampleAt(locateFace(facePoint(face,x,y)))});
  return (location,claims=false)=>{
    const sample=sampler(location,claims);
    const lake=mapOptions.water?sampleLakeSurface?.surface(location):null;
    sample.lakeDepth=lake?.depth??0;
    sample.lakeCoverage=lake?.coverage??0;
    sample.lakeShore=lake?.shore??-1;
    return sample;
  };
}

function globeGeometry() {
  return camera.geometry(canvas.width, canvas.height);
}

function renderInteractiveFrame({started,sampleSurface,centerX,centerY,radius,width,height}){
  // The exact cartographic frame is intentionally not mutated. A bounded
  // low-resolution surface keeps camera input responsive, then release builds
  // one sharp frame with rivers, contours and symbols.
  const targetSamples=18_000,scale=Math.max(4,Math.ceil(Math.sqrt(width*height/targetSamples)));
  const iw=Math.ceil(width/scale),ih=Math.ceil(height/scale);
  if(interactiveInk.width!==iw||interactiveInk.height!==ih){interactiveInk.width=iw;interactiveInk.height=ih;}
  const ctx=interactiveInk.getContext("2d"),image=ctx.createImageData(iw,ih),pixels=image.data;
  const packed=((255<<24)|(background[2]<<16)|(background[1]<<8)|background[0])>>>0;
  new Uint32Array(pixels.buffer).fill(packed);
  const cx=centerX/scale,cy=centerY/scale,r=radius/scale;
  for(let py=Math.max(0,Math.floor(cy-r));py<=Math.min(ih-1,Math.ceil(cy+r));py++)for(let px=Math.max(0,Math.floor(cx-r));px<=Math.min(iw-1,Math.ceil(cx+r));px++){
    const sx=(px+.5-cx)/r,sy=-(py+.5-cy)/r,q=sx*sx+sy*sy;if(q>1)continue;
    const point=rotateViewToWorld(sx,sy,Math.sqrt(1-q)),location=locateFace(point);
    const tx=clamp(Math.floor((location.u+1)*metadata.faceSize/2/metadata.chunkSize),0,chunkAxis-1),ty=clamp(Math.floor((location.v+1)*metadata.faceSize/2/metadata.chunkSize),0,chunkAxis-1);
    const key=faceIds.get(location.face)*chunkAxis*chunkAxis+ty*chunkAxis+tx;
    const tile={face:location.face,tx,ty};
    const sample=sampleSurface(location);let color=terrainColor(sample,point);
    if(mapOptions.water&&(sample.elevation<=metadata.seaLevelMeters||sample.lakeDepth>1)){
      const depth=Math.max(metadata.seaLevelMeters-sample.elevation,sample.lakeDepth-1),amount=clamp(depth/240,0,1);color=[195-42*amount,220-26*amount,226-17*amount];
    }
    const offset=(py*iw+px)*4;pixels[offset]=color[0];pixels[offset+1]=color[1];pixels[offset+2]=color[2];pixels[offset+3]=255;
    for(const dependency of mapData.dependenciesFor(tile))neededChunks.add(dependency);
    neededChunks.add(key);
  }
  ctx.putImageData(image,0,0);
  context.imageSmoothingEnabled=true;context.drawImage(interactiveInk,0,0,iw,ih,0,0,width/pixelRatio,height/pixelRatio);
  context.beginPath();context.arc(centerX/pixelRatio,centerY/pixelRatio,radius/pixelRatio,0,Math.PI*2);context.strokeStyle=dark?"#d8c9ad":"#5d5145";context.lineWidth=1.2;context.stroke();
  if(!globeRenderer.snapshotReady)drawSettlementMarkers(centerX/pixelRatio,centerY/pixelRatio,radius/pixelRatio);
  weatherEffects.update({key:"interactive",width:width/pixelRatio,height:height/pixelRatio,pixelRatio,sites:()=>[],enabled:false,arrows:false,geometry:{centerX:centerX/pixelRatio,centerY:centerY/pixelRatio,radius:radius/pixelRatio}});
  setChunkWorkingSet(neededChunks);updateViewStatus();
  const elapsed=performance.now()-started;document.getElementById("sphere-render-time").textContent=`${Math.round(elapsed)} мс`;canvas.dataset.interactiveMs=elapsed.toFixed(1);
}

function renderWebGlInteraction({started,centerX,centerY,radius,width,height}){
  context.clearRect(0,0,width/pixelRatio,height/pixelRatio);
  // The snapshot already contains settlements and cartographic signs. Drawing
  // them again both doubled the work and made two projections visibly diverge.
  if(!globeRenderer.snapshotReady)drawSettlementMarkers(centerX/pixelRatio,centerY/pixelRatio,radius/pixelRatio);
  weatherEffects.update({key:"gpu-interactive",width:width/pixelRatio,height:height/pixelRatio,pixelRatio,sites:()=>[],enabled:false,arrows:false,
    geometry:{centerX:centerX/pixelRatio,centerY:centerY/pixelRatio,radius:radius/pixelRatio}});
  if(performance.now()-lastInteractiveStatusAt>140){lastInteractiveStatusAt=performance.now();updateViewStatus();}
  const elapsed=performance.now()-started;document.getElementById("sphere-render-time").textContent=`GPU ${elapsed.toFixed(1)} мс`;canvas.dataset.interactiveMs=elapsed.toFixed(1);
}

function currentViewScope() {
  if(!metadata)return {key:"loading",level:"world",cityIds:null,label:"Весь мир",context:"Загрузка пространственных данных"};
  const level=camera.zoom<2.4?"world":camera.zoom<12?"region":"local";
  let visible=metadata.settlements.map(settlement=>{
    const anchor=settlement.buildings[0];if(!anchor)return null;
    const view=rotateWorldToView(facePoint(anchor.face,anchor.x,anchor.y));
    const geometry=globeGeometry(),x=geometry.centerX+view.x*geometry.radius,y=geometry.centerY-view.y*geometry.radius;
    return {settlement,view,x,y,distance:view.x*view.x+view.y*view.y};
  }).filter(item=>item&&item.view.z>.01&&item.x>=-40&&item.x<=canvas.width+40&&item.y>=-40&&item.y<=canvas.height+40);
  if(level==="world")visible=metadata.settlements.map(settlement=>({settlement}));
  if(level==="local"&&visible.length>1)visible=[visible.sort((a,b)=>a.distance-b.distance)[0]];
  const ids=level==="world"?null:visible.map(item=>item.settlement.id).sort();
  const label=level==="world"?"Весь мир":level==="region"
    ? (visible.length?`Регион · ${visible.length} пос.`:"Ненаселённый регион")
    : (visible[0]?.settlement.name??"Окрестности без поселений");
  const context=level==="world"?"Глобальная сводка — локальные события скрыты":level==="region"
    ? "Сводка по поселениям, попавшим в экран":"Местные запасы, решения и события";
  return {key:`${level}:${ids?.join(",")??"all"}`,level,cityIds:ids,label,context};
}

function updateWeatherSummary(){
  const values=[],locations=[];
  if(sampleWeather)for(let y=0;y<4;y++)for(let x=0;x<6;x++){
    const point=camera.worldAt((x+.5)*canvas.width/6,(y+.5)*canvas.height/4,canvas.width,canvas.height);
    if(point){const location=locateFace(point);locations.push(location);values.push(sampleWeather(location));}
  }
  const average=key=>values.length?values.reduce((sum,item)=>sum+item[key],0)/values.length:NaN;
  const set=(id,value,digits=1)=>document.getElementById(id).textContent=Number.isFinite(value)?value.toFixed(digits):"—";
  set("sphere-weather-temperature",average("temperature"));set("sphere-weather-rain",average("rain"));
  const wind=values.length?values.reduce((sum,item)=>sum+Math.hypot(item.windX,item.windY,item.windZ),0)/values.length:NaN;
  set("sphere-weather-wind",wind,2);
  set("sphere-weather-snow",average("snow"),0);
  document.getElementById("sphere-weather-scope").textContent=`Погода · ${currentViewScope().label.toLowerCase()}`;
  currentClimateSeries=climateSeries(sampleClimate,locations);renderClimateHistory();
}

const climateMonths=["янв","фев","мар","апр","май","июн","июл","авг","сен","окт","ноя","дек"];
function renderClimateHistory(){
  const chart=document.getElementById("sphere-climate-chart");if(!chart)return;chart.replaceChildren();chart.dataset.field=climateField;
  const present=currentClimateSeries.filter(item=>item[climateField]!==null),values=present.map(item=>item[climateField]);
  const min=values.length?Math.min(...values):0,max=values.length?Math.max(...values):1,range=Math.max(.001,max-min);
  const digits=climateField==="wind"?2:1,unit=climateField==="temperature"?"°":climateField==="rain"?" мм":"";
  for(let month=0;month<12;month++){
    const item=currentClimateSeries[month],value=item?.[climateField];
    const column=document.createElement("div");column.className="sphere-climate-month"+(value===null||value===undefined?" is-missing":"");
    const label=document.createElement("strong"),track=document.createElement("div"),bar=document.createElement("i"),name=document.createElement("small");
    track.className="sphere-climate-track";bar.className="sphere-climate-bar";name.textContent=climateMonths[month];
    if(value===null||value===undefined)label.textContent="—";
    else{label.textContent=`${value.toFixed(digits)}${unit}`;bar.style.height=`${Math.round(5+27*(value-min)/range)}px`;track.append(bar);}
    column.append(label,track,name);chart.append(column);
  }
  const observed=currentClimateSeries.filter(item=>item.sampleDays>0).length;
  const maxDays=Math.max(0,...currentClimateSeries.map(item=>item.sampleDays));
  document.getElementById("sphere-climate-note").textContent=observed?`История области · ${observed}/12 мес. · до ${maxDays} дн.`:"История появится по мере симуляции";
}

for(const button of document.querySelectorAll("[data-climate-field]"))button.addEventListener("click",()=>{
  climateField=button.dataset.climateField;
  for(const peer of document.querySelectorAll("[data-climate-field]"))peer.setAttribute("aria-selected",String(peer===button));
  renderClimateHistory();
});
const climateHistory=document.getElementById("sphere-climate-history"),climateToggle=document.getElementById("sphere-climate-toggle");
function setClimateCollapsed(collapsed){
  climateHistory.classList.toggle("is-collapsed",collapsed);climateToggle.setAttribute("aria-expanded",String(!collapsed));
  climateToggle.querySelector("span").textContent=collapsed?"Показать климатограмму":"Климатограмма";
  try{localStorage.setItem("worldgen.climateCollapsed",String(collapsed));}catch{}
}
try{setClimateCollapsed(localStorage.getItem("worldgen.climateCollapsed")==="true");}catch{setClimateCollapsed(false);}
climateToggle.addEventListener("click",()=>setClimateCollapsed(!climateHistory.classList.contains("is-collapsed")));

function scheduleInterfaceWork(key,work,delay=120){
  interfaceWork.set(key,work);clearTimeout(interfaceWorkTimer);
  if(interfaceIdle&&window.cancelIdleCallback){cancelIdleCallback(interfaceIdle);interfaceIdle=0;}
  interfaceWorkTimer=setTimeout(flushInterfaceWork,delay);
}

function flushInterfaceWork(){
  interfaceWorkTimer=null;
  if(drag||cameraInteraction||zoomMotion||frame!==null||simulationExactPending){interfaceWorkTimer=setTimeout(flushInterfaceWork,120);return;}
  const run=()=>{
    interfaceIdle=0;
    if(drag||cameraInteraction||zoomMotion||frame!==null||simulationExactPending){interfaceWorkTimer=setTimeout(flushInterfaceWork,120);return;}
    const started=performance.now(),tasks=[...interfaceWork.values()];interfaceWork.clear();
    for(const task of tasks)task();canvas.dataset.interfaceMs=(performance.now()-started).toFixed(1);
  };
  if(window.requestIdleCallback)interfaceIdle=requestIdleCallback(run,{timeout:700});else setTimeout(run,0);
}

function scheduleScopeRefresh(delay=180){
  scheduleInterfaceWork("scope",()=>{updateWeatherSummary();simulationController?.refreshScope();},delay);
}

function advanceZoomMotion(timestamp){
  if(!zoomMotion)return false;
  const elapsed=clamp(timestamp-zoomMotion.lastTime,1,48);zoomMotion.lastTime=timestamp;
  const next=dampZoom(camera.zoom,zoomMotion.targetZoom,elapsed);
  camera.zoomAt(next,zoomMotion.x,zoomMotion.y,zoomMotion.width,zoomMotion.height);
  const finished=Math.abs(Math.log(camera.zoom/zoomMotion.targetZoom))<.00045;
  if(finished){
    camera.zoomAt(zoomMotion.targetZoom,zoomMotion.x,zoomMotion.y,zoomMotion.width,zoomMotion.height);
    zoomMotion=null;canvas.dataset.zoomAnimating="false";scheduleScopeRefresh();return false;
  }
  canvas.dataset.zoomAnimating="true";return true;
}

function renderSphere(timestamp=performance.now()) {
  const started=performance.now();
  frame = null;
  if(advanceZoomMotion(timestamp))scheduleRender();
  if (!preview) return;
  neededChunks = new Set();
  const cartographic = ["topographic","elevation","political"].includes(layerSelect.value);
  const interacting=!!drag||cameraInteraction;
  const preciseCartography=cartographic&&!interacting;
  const sampleSurface = smoothSampler();
  let visibleTiles=new Map();
  const { centerX, centerY, radius } = globeGeometry();
  mobileUnits.render();
  const width = canvas.width;
  const height = canvas.height;
  const gpuFrame=globeRenderer.draw({width,height,centerX,centerY,radius,matrix:camera.matrix,useSnapshot:interacting});
  if(interacting&&gpuFrame){renderWebGlInteraction({started,centerX,centerY,radius,width,height});return;}
  if(interacting){renderInteractiveFrame({started,sampleSurface,centerX,centerY,radius,width,height});return;}
  const step = 1;
  const rasterKey=JSON.stringify([width,height,camera.zoom,camera.orientation,layerSelect.value,step,mapOptions.water,hydrologyVersion(),metadata.worldId]);
  const reuseRaster=rasterSnapshot?.key===rasterKey&&[...rasterSnapshot.visibleTiles].every(([key,tile])=>rasterSnapshot.versions.get(key)===tileVersion(tile,true));
  let image,mask;
  if(reuseRaster){
    ({image,mask,visibleTiles}=rasterSnapshot);neededChunks=new Set(rasterSnapshot.neededChunks);
  }else{
  image = context.createImageData(width, height);
  const pixels = image.data;
  mask=preciseCartography?context.createImageData(width,height):null;
  const ocean=preciseCartography?new Float32Array(width*height).fill(NaN):null;
  const lakes=preciseCartography?new Float32Array(width*height).fill(NaN):null;
  const waterColors=preciseCartography?new Uint8ClampedArray(width*height*3):null;
  if(!gpuFrame){const packed=((255<<24)|(background[2]<<16)|(background[1]<<8)|background[0])>>>0;new Uint32Array(pixels.buffer).fill(packed);}
  const x0 = Math.max(0, Math.floor(centerX - radius));
  const x1 = Math.min(width - 1, Math.ceil(centerX + radius));
  const y0 = Math.max(0, Math.floor(centerY - radius));
  const y1 = Math.min(height - 1, Math.ceil(centerY + radius));
  for (let py = y0; py <= y1; py += step) {
    const sy = -(py + 0.5 - centerY) / radius;
    for (let px = x0; px <= x1; px += step) {
      const sx = (px + 0.5 - centerX) / radius;
      const squared = sx * sx + sy * sy;
      if (squared > 1) continue;
      const point = rotateViewToWorld(sx, sy, Math.sqrt(1 - squared));
      const location=locateFace(point);
      {
        const tx=clamp(Math.floor((location.u+1)*metadata.faceSize/2/metadata.chunkSize),0,chunkAxis-1);
        const ty=clamp(Math.floor((location.v+1)*metadata.faceSize/2/metadata.chunkSize),0,chunkAxis-1);
        const key=faceIds.get(location.face)*chunkAxis*chunkAxis+ty*chunkAxis+tx;
        if(!visibleTiles.has(key))visibleTiles.set(key,{face:location.face,tx,ty});
      }
      const sample = cartographic ? sampleSurface(location) : sampleAt(location);
      let color = terrainColor(sample, point);
      if(cartographic&&drag&&mapOptions.water&&(sample.elevation<=metadata.seaLevelMeters||sample.lakeDepth>1)){
        const depth=Math.max(metadata.seaLevelMeters-sample.elevation,sample.lakeDepth-1),amount=clamp(depth/240,0,1);
        color=[195-42*amount,220-26*amount,226-17*amount];
      }
      if(preciseCartography){
        const index=py*width+px;
        ocean[index]=metadata.seaLevelMeters-sample.elevation;
        lakes[index]=sample.lakeShore;
        const depth=Math.max(ocean[index],sample.lakeDepth-1),amount=clamp(depth/240,0,1);
        const lighting=camera.zoom<2?.93+Math.max(0,point.z*.7+point.y*.3)*.07:1;
        waterColors[index*3]=(195-42*amount)*lighting;
        waterColors[index*3+1]=(220-26*amount)*lighting;
        waterColors[index*3+2]=(226-17*amount)*lighting;
      }
      for (let dy = 0; dy < step && py + dy < height; dy++) {
        for (let dx = 0; dx < step && px + dx < width; dx++) {
          const pixel = (py + dy) * width + px + dx;
          const offset = pixel * 4;
          pixels[offset] = color[0];
          pixels[offset + 1] = color[1];
          pixels[offset + 2] = color[2];
          pixels[offset + 3] = 255;
        }
      }
    }
  }
  if(preciseCartography)paintWaterRaster({pixels,mask:mask.data,ocean,lakes,waterColors,width,height,step,x0,y0,x1,y1,pixelRatio,
    fill:layerSelect.value==="topographic",stroke:layerSelect.value!=="political",
    shoreColor:atlas?atlas.palette.water.match(/[0-9a-f]{2}/gi).map(value=>parseInt(value,16)):undefined});
  rasterSnapshot={key:rasterKey,image,mask,visibleTiles,neededChunks:new Set(neededChunks),
    versions:new Map([...visibleTiles].map(([key,tile])=>[key,tileVersion(tile,true)]))};
  rasterBuilds++;
  }
  context.putImageData(image, 0, 0);
  const cssX=centerX/pixelRatio,cssY=centerY/pixelRatio,cssRadius=radius/pixelRatio;
  const cssWidth=width/pixelRatio,cssHeight=height/pixelRatio;
  const rayAt=(x,y)=>{
    const sx=(x-cssX)/cssRadius,sy=-(y-cssY)/cssRadius,q=sx*sx+sy*sy;
    return q>1?null:rotateViewToWorld(sx,sy,Math.sqrt(1-q));
  };
  const weatherKey=JSON.stringify([rasterKey,rasterBuilds,simulationView?.weatherMap?.revision,weatherLayer.value,mapOptions.winter]);
  context.save();context.beginPath();context.arc(cssX,cssY,cssRadius,0,Math.PI*2);context.clip();
  weatherTint.draw(context,{key:weatherKey,width:cssWidth,height:cssHeight,rayAt,sample:sampleWeather,mode:weatherLayer.value,
    winter:mapOptions.winter&&layerSelect.value==='topographic',
    landAt:(x,y)=>mask?mask.data[(Math.min(height-1,Math.floor(y*pixelRatio))*width+Math.min(width-1,Math.floor(x*pixelRatio)))*4+3]/255:1});
  context.restore();
  weatherEffects.update({key:weatherKey,width:cssWidth,height:cssHeight,pixelRatio,
    sites:()=>weatherEffectSites({width:cssWidth,height:cssHeight,rayAt,sample:sampleWeather,toView:rotateWorldToView}),
    enabled:weatherMotion.checked&&!!sampleWeather,arrows:weatherLayer.value==='wind',
    geometry:{centerX:cssX,centerY:cssY,radius:cssRadius}});
  canvas.dataset.weatherBuilds=String(weatherTint.builds);
  canvas.dataset.weatherMs=weatherTint.milliseconds.toFixed(1);
  if(preciseCartography && atlas) {
    for(const surface of [landInk,mapInk,waterMask]){surface.width=width;surface.height=height;}
    const ink=landInk.getContext("2d"),overlay=mapInk.getContext("2d"),maskContext=waterMask.getContext("2d");
    ink.setTransform(pixelRatio,0,0,pixelRatio,0,0);overlay.setTransform(pixelRatio,0,0,pixelRatio,0,0);
    maskContext.putImageData(mask,0,0);
    context.save();
    context.beginPath();context.arc(cssX,cssY,cssRadius,0,Math.PI*2);context.clip();
    const project=point=>{
      const view=rotateWorldToView(point);
      return view.z<=0 ? null : {x:cssX+view.x*cssRadius,y:cssY-view.y*cssRadius,z:view.z};
    };
    const geometryStats=drawCartographicLayer({context:overlay,riverContext:ink,width:width/pixelRatio,height:height/pixelRatio,zoom:camera.zoom,
      metadata,atlas,hydrology,drawSymbol,options:mapOptions,layer:layerSelect.value,simulation:simulationView,
      sampleWeather:point=>sampleWeather?.(locateFace(point)),
      tiles:[...visibleTiles.values()],dataVersion:tile=>tileVersion(tile,true),staticVersion:tile=>tileVersion(tile),
      sampleWorld:(cell,claims=false)=>sampleSurface(locateFace(facePoint(cell.face,cell.x,cell.y)),claims),
      projectCell:cell=>project(facePoint(cell.face,cell.x,cell.y)),
      projectVector:point=>project({x:point[0],y:point[1],z:point[2]})
    });
    ink.setTransform(1,0,0,1,0,0);ink.globalCompositeOperation="destination-in";ink.drawImage(waterMask,0,0);
    context.drawImage(landInk,0,0,width/pixelRatio,height/pixelRatio);
    context.drawImage(mapInk,0,0,width/pixelRatio,height/pixelRatio);
    document.getElementById("sphere-geometry-builds").textContent=String(geometryStats.builds);
    document.getElementById("sphere-land-builds").textContent=String(geometryStats.landBuilds);
    context.restore();
  }
  context.beginPath();
  context.arc(centerX / pixelRatio, centerY / pixelRatio, radius / pixelRatio, 0, Math.PI * 2);
  context.strokeStyle = dark ? "#d8c9ad" : "#5d5145";
  context.lineWidth = 1.2;
  context.stroke();
  drawSettlementMarkers(centerX / pixelRatio, centerY / pixelRatio, radius / pixelRatio);
  // Cached geometry still depends on its halo: keep those chunks resident even
  // when no sampling was necessary in this frame.
  if(camera.zoom>=3.5)for(const tile of visibleTiles.values())
    for(const key of mapData.dependenciesFor(tile))neededChunks.add(key);
  rasterSnapshot.neededChunks=new Set(neededChunks);
  setChunkWorkingSet(neededChunks);
  const snapshotStatus=chunkCache.status;
  if(snapshotStatus.loaded===snapshotStatus.total&&snapshotStatus.pending===0)
    globeRenderer.captureSnapshot(canvas,{width,height,centerX,centerY,radius,matrix:camera.matrix});
  updateViewStatus();
  const elapsed=performance.now()-started;
  document.getElementById("sphere-render-time").textContent=`${Math.round(elapsed)} мс`;
  canvas.dataset[interacting?"interactiveMs":"finalMs"]=elapsed.toFixed(1);
  document.getElementById("sphere-raster-builds").textContent=String(rasterBuilds);
  if(!interacting&&simulationExactPending){
    simulationExactPending=false;clearTimeout(simulationReconcileTimer);simulationReconcileTimer=null;
  }
}

function facePoint(face, x, y) {
  return surfacePoint(face,x,y,metadata.faceSize);
}

function rotateWorldToView(point) {
  return camera.toView(point);
}

function drawSettlementMarkers(centerX, centerY, radius) {
  context.font = "600 11px Inter, system-ui, sans-serif";
  context.textBaseline = "middle";
  for (let index = 0; index < metadata.settlements.length; index++) {
    const settlement = metadata.settlements[index];
    if (camera.zoom >= 8) {
      for (const land of settlement.usedLands) drawCellOutline(land, land.usage > 0 ? "#ac995d" : "#838c82", centerX, centerY, radius, true);
      const outlined=new Set();
      for (const building of settlement.buildings) {
        if(layerSelect.value==="topographic"&&building.buildingTypeId==="garden"&&building.status==="active")continue;
        const key=`${building.face}:${building.x}:${building.y}`;if(outlined.has(key))continue;outlined.add(key);
        drawCellOutline(building, layerSelect.value === "topographic" ? "rgba(135,143,116,.55)" : "#d7d4c4", centerX, centerY, radius, false);
      }
    }
    const anchor = settlement.buildings[0];
    if (!anchor) continue;
    const view = rotateWorldToView(facePoint(anchor.face, anchor.x, anchor.y));
    if (view.z <= 0.02) continue;
    const x = centerX + view.x * radius;
    const y = centerY - view.y * radius;
    if (x < -160 || x > canvas.width / pixelRatio + 20 || y < -20 || y > canvas.height / pixelRatio + 20) continue;
    const color = settlementColors[index % settlementColors.length];
    if(camera.zoom < 8 || layerSelect.value !== "topographic" || !mapOptions.symbols) {
      context.beginPath(); context.arc(x, y, 4.5, 0, Math.PI * 2);
      context.fillStyle = `rgb(${color.join(" ")})`;context.fill();
      context.strokeStyle = "#39443a";context.lineWidth = 1.2;context.stroke();
    }
    const footprintViews=settlement.buildings.filter(building=>building.id===anchor.id)
      .map(building=>rotateWorldToView(facePoint(building.face,building.x,building.y))).filter(view=>view.z>0);
    const labelX=camera.zoom>=8 ? Math.max(x,...footprintViews.map(view=>centerX+view.x*radius))+17 : x+8;
    context.strokeStyle = layerSelect.value === "topographic" ? "#f4f0df" : dark ? "#19201e" : "#fffdf7";
    context.lineWidth = 3;
    context.strokeText(settlement.name, labelX, y);
    context.fillStyle = layerSelect.value === "topographic" ? "#39443a" : dark ? "#edf2ef" : "#202725";
    context.fillText(settlement.name, labelX, y);
  }
  if (selectedCell) drawCellOutline(selectedCell, "#64f0d1", centerX, centerY, radius, false);
}

function drawCellOutline(cell, color, centerX, centerY, radius, dashed) {
  const points = [[0,0],[1,0],[1,1],[0,1]].map(([dx,dy]) =>
    rotateWorldToView(facePoint(cell.face, cell.x + dx - 0.5, cell.y + dy - 0.5)));
  if (points.some(point => point.z <= 0)) return;
  context.beginPath();
  points.forEach((point, index) => {
    const x = centerX + point.x * radius;
    const y = centerY - point.y * radius;
    if (index === 0) context.moveTo(x,y); else context.lineTo(x,y);
  });
  context.closePath();
  context.strokeStyle = color;
  context.lineWidth = selectedCell === cell ? 2 : .85;
  context.setLineDash(dashed ? [4,3] : []);
  context.stroke();
  context.setLineDash([]);
}

function updateViewStatus() {
  if (!metadata) return;
  document.getElementById("sphere-zoom-level").textContent = `${camera.zoom.toFixed(1)}×`;
  document.getElementById("sphere-zoom-out").disabled = camera.zoom <= MIN_SPHERE_ZOOM;
  document.getElementById("sphere-zoom-in").disabled = camera.zoom >= MAX_SPHERE_ZOOM;
  const status = chunkCache.status;
  document.getElementById("sphere-lod").textContent = camera.zoom < 3.5 ? "обзор 1:4" :
    status.loaded === status.total ? "зоны 1:1" : `1:1 · загрузка ${status.loaded}/${status.total}`;
  document.getElementById("sphere-loaded").textContent = `${status.resident}/192`;
  document.getElementById("sphere-chunk-requests").textContent = String(chunkRequests);
  document.getElementById("sphere-contour-interval").textContent = `${contourInterval(camera.zoom)} м`;
  document.getElementById("sphere-retry").hidden = status.failed === 0;
  document.getElementById("sphere-view-error").textContent = status.failed ? `Не загружено чанков: ${status.failed}. Доступен обзор; повторите загрузку.` : "";
}

function hideTooltip() { clearTimeout(tooltipTimer); tooltip.hidden = true; }

function applyMapUpdate(update,{refresh=true}={}) {
  const result=mapData.apply(update);
  if(!result.accepted)return false;
  if(result.structures){
    mapData.markTiles(structureTiles(metadata.settlements,result.structures,mapData));
    metadata.settlements=result.structures;refreshParcelOptions();
  }
  metadata.revision=update.revision;
  document.getElementById("sphere-map-updates").textContent=String(mapData.updates);
  if(refresh)refreshSelection();
  return result;
}

function applySimulationView(state,{live=false}={}){
  if(simulationView&&state.revision<simulationView.revision){canvas.dataset.rejectedSimulationRevision=String(state.revision);return false;}
  canvas.dataset.simulationRevision=String(state.revision);
  // Most live frames contain only simulation data. A rare structural frame
  // carries buildings/fields/claims and earns one exact cartographic redraw.
  if(!live&&!applyMapUpdate(state.map,{refresh:true}))return false;
  if(live&&state._liveMapChanged){
    const mapResult=applyMapUpdate(state.map,{refresh:false});if(!mapResult)return false;
    simulationExactPending=mapResult.changedTiles.size>0||!!mapResult.structures;
    if(simulationExactPending)scheduleSimulationReconcile();
  }
  simulationView=state;
  mobileUnits.update(state.scouts??[],state.day,{animate:live});
  sampleWeather=createWeatherSampler(state.weatherMap,metadata.faceSize);
  sampleClimate=createClimateSampler(state.weatherMap);
  const homes=new Map(state.cities.flatMap(city=>city.homes??[]).map(home=>[home.id,home]));
  for(const city of metadata.settlements)for(const building of city.buildings){
    const home=homes.get(building.id);if(home){building.status=home.status;building.residents=home.residents;}
  }
  if(live)scheduleInterfaceWork("weather",()=>{updateWeatherLegend();updateWeatherSummary();},90);
  else{
    updateWeatherLegend();
    simulationExactPending=false;clearTimeout(simulationReconcileTimer);simulationReconcileTimer=null;
    refreshSelection();scheduleRender();scheduleScopeRefresh(0);
  }
}

function updateWeatherLegend(){
  const data=simulationView?.weatherMap;
  const scales={none:'Без цветной подложки',temperature:'Синий −15 °C → охра +30 °C',rain:'Синий: 0–12+ мм/сут',wind:'Стрелки — направление; насыщенность — условная сила'};
  document.getElementById('sphere-weather-legend').textContent=data?
    `${scales[weatherLayer.value]}. Погода дня ${Math.max(0,data.revision)}; обзор ${data.resolution}×${data.resolution} на грань, снег и лёд у поселений — местные. Лёд на обзоре не означает доступный переход.`:
    'Погодный слой доступен в первобытном сценарии с зимним расчётом.';
}

weatherLayer.addEventListener('change',()=>{updateWeatherLegend();scheduleRender();});
weatherMotion.addEventListener('change',()=>scheduleRender());
document.addEventListener('visibilitychange',()=>weatherEffects.refresh());
window.matchMedia('(prefers-reduced-motion: reduce)').addEventListener('change',()=>weatherEffects.refresh());
window.addEventListener('pagehide',()=>weatherEffects.stop());
window.addEventListener('pageshow',()=>weatherEffects.refresh());
new IntersectionObserver(entries=>{weatherInView=entries[0].isIntersecting;weatherEffects.refresh();}).observe(canvas);

async function refreshSphereData() {
  if (refreshPromise) return refreshPromise;
  refreshPromise = (async () => {
    const response=await fetch(`/api/sphere/map?${mapData.query()}`,{cache:"no-store"});
    if(!response.ok)throw new Error("Не удалось обновить карту — повторите загрузку");
    if(applyMapUpdate(await response.json()))scheduleRender();
  })();
  try { await refreshPromise; } finally { refreshPromise = null; }
}

function selectedLand() {
  if (!selectedCell) return null;
  return metadata.settlements.flatMap(settlement => settlement.usedLands).find(land =>
    land.face === selectedCell.face && land.x === selectedCell.x && land.y === selectedCell.y);
}

function selectCell(event) {
  const location = screenLocation(event.clientX, event.clientY);
  if (!location) return;
  const sample = sampleAt(location);
  selectedCell = { face: location.face, x: sample.cellX, y: sample.cellY };
  hideTooltip();
  refreshSelection();
}

function refreshSelection() {
  if (!selectedCell || !metadata) return;
  const sample = sampleAt(locateFace(facePoint(selectedCell.face, selectedCell.x, selectedCell.y)));
  const land = selectedLand();
  const buildings = metadata.settlements.flatMap(settlement => settlement.buildings).filter(building =>
    building.face === selectedCell.face && building.x === selectedCell.x && building.y === selectedCell.y);
  const owner = sample.owner >= 0 ? metadata.settlements[sample.owner].name : "нет влияния поселений";
  const lakeDepth=lakeDepthAt(locateFace(facePoint(selectedCell.face,selectedCell.x,selectedCell.y)));
  const industries=simulationView?.cities.flatMap(city=>city.industries).filter(site=>site.face===selectedCell.face&&site.x===selectedCell.x&&site.y===selectedCell.y)??[];
  selection.textContent = `Грань ${faceLabels[selectedCell.face]} · зона ${selectedCell.x}:${selectedCell.y} · ` +
    `${biomeNames[sample.biome]}, ${Math.round(sample.elevation)} м · ${owner}. ` +
    `${sample.exact ? "Точные данные 1:1." : "Пока показаны обзорные данные 1:4."} ` +
    (lakeDepth>1 ? `Расчётная озёрная впадина (гидрология 1:${hydrology.stride}). ` : "") +
    (buildings.length ? `Постройки: ${buildings.map(item => `${buildingNames[item.buildingTypeId]??item.buildingTypeId} (${buildingStates[item.status]??"действует"}${item.buildingTypeId==="house"?`, ${item.residents}/25 жителей`:""})`).join(", ")}; занято ${buildings.reduce((sum,item) => sum + item.capacityUnits, 0)}/4 ед. ` : "") +
    (land ? `Угодье ${land.id}: использование ${Math.round(land.usage * 100)}%. ` : "") +
    industries.map(site=>`${site.name}: ${site.totalBatches.toFixed(2)} партий; лес ${Math.round(site.forestBiomass*100)}%, почва ${Math.round(site.soilQuality*100)}%. ${site.blockedReason??site.lastConstraintKey??""}`).join(" ") +
    (simulationView?.cities.flatMap(city=>city.homes??[]).filter(b=>b.face===selectedCell.face&&b.x===selectedCell.x&&b.y===selectedCell.y)
      .map(b=>buildingConditionText(b,simulationView.lifecycleRules)).join("; ")??"");
  if(sampleWeather)selection.textContent+=' '+weatherText(sampleWeather(locateFace(facePoint(selectedCell.face,selectedCell.x,selectedCell.y))),{water:sample.elevation<=metadata.seaLevelMeters||lakeDepth>1});
  document.getElementById("sphere-land-controls").hidden = !land;
  document.getElementById("sphere-land-toggle").textContent = land?.usage > 0 ? "Прекратить использование угодья" : "Возобновить использование угодья";
}

function beginCameraInteraction(settleDelay=150) {
  cameraInteraction=true;
  clearTimeout(chunkRenderTimer);
  clearTimeout(cameraSettleTimer);
  const settle=()=>{
    if(zoomMotion){cameraSettleTimer=setTimeout(settle,45);return;}
    cameraInteraction=false;scheduleRender();
  };
  cameraSettleTimer=setTimeout(settle,settleDelay);
}

function zoomAt(value, x, y, interactive=false) {
  const rect = canvas.getBoundingClientRect();
  const pointX=x??rect.width*.5,pointY=y??rect.height*.49;
  if(interactive){
    beginCameraInteraction();
    zoomMotion??={targetZoom:camera.zoom,lastTime:performance.now(),x:pointX,y:pointY,width:rect.width,height:rect.height};
    Object.assign(zoomMotion,{targetZoom:clamp(value,MIN_SPHERE_ZOOM,MAX_SPHERE_ZOOM),x:pointX,y:pointY,width:rect.width,height:rect.height});
    hideTooltip();scheduleRender();return;
  }
  camera.zoomAt(value,pointX,pointY,rect.width,rect.height);
  hideTooltip();
  scheduleRender();scheduleScopeRefresh();
}

function cancelZoomMotion(){
  zoomMotion=null;cameraInteraction=false;clearTimeout(cameraSettleTimer);canvas.dataset.zoomAnimating="false";
}

function requestedZoom(){return zoomMotion?.targetZoom??camera.zoom;}

function scheduleRender() {
  if (frame !== null) return;
  frame = requestAnimationFrame(renderSphere);
}

function scheduleSimulationReconcile(delay=650){
  clearTimeout(simulationReconcileTimer);
  simulationReconcileTimer=setTimeout(()=>{
    simulationReconcileTimer=null;
    if(!simulationExactPending)return;
    // Camera input and exact chunk arrival have priority over background world
    // reconciliation. Keep the accepted data dirty and try again when quiet.
    if(drag||cameraInteraction||zoomMotion||chunkCache.pending.size){scheduleSimulationReconcile(180);return;}
    scheduleRender();
  },delay);
}

function screenLocation(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const { centerX, centerY, radius } = globeGeometry();
  const px = (clientX - rect.left) * pixelRatio;
  const py = (clientY - rect.top) * pixelRatio;
  const sx = (px - centerX) / radius;
  const sy = -(py - centerY) / radius;
  const squared = sx * sx + sy * sy;
  if (squared > 1) return null;
  const point = rotateViewToWorld(sx, sy, Math.sqrt(1 - squared));
  return { ...locateFace(point), point };
}

function showTooltip(location, event) {
  if (!preview || drag || cameraInteraction) return;
  const sample = sampleAt(location);
  const cellX = clamp(Math.floor((location.u + 1) * 0.5 * metadata.faceSize), 0, metadata.faceSize - 1);
  const cellY = clamp(Math.floor((location.v + 1) * 0.5 * metadata.faceSize), 0, metadata.faceSize - 1);
  tooltip.replaceChildren();
  const title = document.createElement("strong");
  title.textContent = `Грань ${faceLabels[location.face]} · зона ${cellX}:${cellY}${sample.exact ? "" : " · обзорные данные 1:4"}`;
  const detail = document.createElement("span");
  const owner = sample.owner >= 0 ? ` · влияние: ${metadata.settlements[sample.owner].name} ${Math.round(sample.influence * 100)}%` : "";
  detail.textContent = `${biomeNames[sample.biome]} · ${Math.round(sample.elevation)} м · ` +
    `влажность ${Math.round(sample.moisture * 100)}% · лес ${Math.round(sample.forest * 100)}%${owner}` +
    (lakeDepthAt(location)>1 ? ` · расчётное озеро (сток 1:${hydrology?.stride??4})` : "");
  tooltip.append(title, detail);
  if(sample.elevation>metadata.seaLevelMeters&&lakeDepthAt(location)<=1){const plants=wildPlants(metadata.biosphere,metadata.seed,location.point,sample);if(plants.length){const flora=document.createElement('span');flora.textContent='Дикие растения (обзор): '+plants.map(c=>c.name).join(', ');tooltip.append(flora);}}
  if(sampleWeather){const weather=document.createElement('span');weather.className='tooltip-weather';weather.textContent=weatherText(sampleWeather(location),{water:sample.elevation<=metadata.seaLevelMeters||lakeDepthAt(location)>1});tooltip.append(weather);}
  const local=simulationView?.cities.flatMap(city=>city.homes??[]).filter(b=>b.face===location.face&&b.x===cellX&&b.y===cellY)??[];
  for(const b of local.slice(0,2)){const state=document.createElement("span");state.textContent=buildingHoverText(b);tooltip.append(state);}
  if(local.length>2){const more=document.createElement("span");more.textContent=`Ещё объектов: ${local.length-2}. Нажмите на зону для подробностей.`;tooltip.append(more);}
  const rect = canvas.parentElement.getBoundingClientRect();
  tooltip.style.left = `${clamp(event.clientX - rect.left + 14, 8, rect.width - 280)}px`;
  tooltip.hidden = false;
  tooltip.style.top = `${clamp(event.clientY - rect.top + 14, 8, Math.max(8,rect.height-tooltip.offsetHeight-8))}px`;
  if (!selectedCell) selection.textContent = `${title.textContent} · ${detail.textContent}`;
}

function resize() {
  cancelZoomMotion();
  const rect = canvas.getBoundingClientRect();
  const cssPixels=Math.max(1,rect.width*rect.height);
  pixelRatio = Math.min(1.25, window.devicePixelRatio || 1, Math.sqrt(2_100_000/cssPixels));
  canvas.width = Math.max(1, Math.round(rect.width * pixelRatio));
  canvas.height = Math.max(1, Math.round(rect.height * pixelRatio));
  mobileUnits.resize(rect.width,rect.height,pixelRatio);
  context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
  drag = null;
  canvas.classList.remove("is-dragging");
  hideTooltip();
  scheduleRender();scheduleScopeRefresh();
}

canvas.addEventListener("pointerdown", event => {
  if (event.button !== 0 || !preview) return;
  cancelZoomMotion();
  const rect = canvas.getBoundingClientRect();
  drag = { id: event.pointerId, x: event.clientX, y: event.clientY, moved: false,
    start: camera.beginDrag(event.clientX - rect.left, event.clientY - rect.top, rect.width, rect.height) };
  canvas.setPointerCapture(event.pointerId);
  canvas.classList.add("is-dragging");
  hideTooltip();
});

canvas.addEventListener("pointermove", event => {
  if (!preview) return;
  if (drag?.id === event.pointerId) {
    if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) > 3) drag.moved = true;
    if (!drag.moved) return;
    const rect = canvas.getBoundingClientRect();
    camera.drag(drag.start, event.clientX - rect.left, event.clientY - rect.top, rect.width, rect.height);
    scheduleRender();
    return;
  }
  lastHover = { location: screenLocation(event.clientX, event.clientY), event: { clientX: event.clientX, clientY: event.clientY } };
  clearTimeout(tooltipTimer);
  tooltip.hidden = true;
  if (lastHover.location) tooltipTimer = setTimeout(() => showTooltip(lastHover.location, lastHover.event), 430);
});

function finishDrag(event) {
  if (drag?.id !== event.pointerId) return;
  const click = !drag.moved && event.type === "pointerup";
  const moved=drag.moved;
  drag = null;
  canvas.classList.remove("is-dragging");
  if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
  if (click) selectCell(event);
  if(moved)beginCameraInteraction(110);
  scheduleRender();scheduleScopeRefresh();
}
canvas.addEventListener("pointerup", finishDrag);
canvas.addEventListener("pointercancel", finishDrag);
canvas.addEventListener("lostpointercapture", () => { drag = null; canvas.classList.remove("is-dragging"); scheduleRender(); });
canvas.addEventListener("pointerleave", () => {
  clearTimeout(tooltipTimer);
  tooltip.hidden = true;
});
canvas.addEventListener("wheel", event => {
  event.preventDefault();
  if (drag) return;
  const rect = canvas.getBoundingClientRect();
  const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? rect.height : 1);
  zoomAt(requestedZoom() * Math.exp(-clamp(delta, -600, 600) * 0.002), event.clientX - rect.left, event.clientY - rect.top, true);
}, { passive: false });

document.getElementById("sphere-zoom-in").addEventListener("click", () => zoomAt(requestedZoom() * 1.5,undefined,undefined,true));
document.getElementById("sphere-zoom-out").addEventListener("click", () => zoomAt(requestedZoom() / 1.5,undefined,undefined,true));
document.getElementById("sphere-reset").addEventListener("click", () => { cancelZoomMotion();camera.reset();hideTooltip();scheduleRender();scheduleScopeRefresh(); });
document.getElementById("sphere-retry").addEventListener("click", () => chunkCache.retry());
canvas.addEventListener("dblclick", event => {
  const location = screenLocation(event.clientX, event.clientY);
  if (location) { cancelZoomMotion();camera.focus(location.point, Math.max(8, camera.zoom * 2));hideTooltip();scheduleRender(); }
});
layerSelect.addEventListener("change", scheduleRender);
const journal=document.getElementById("sphere-journal"),journalToggle=document.getElementById("sphere-journal-toggle");
function setJournalCollapsed(collapsed){
  journal.classList.toggle("is-collapsed",collapsed);journalToggle.setAttribute("aria-expanded",String(!collapsed));
  journalToggle.setAttribute("aria-label",collapsed?"Развернуть журнал":"Свернуть журнал");
  try{localStorage.setItem("worldgen.journalCollapsed",collapsed?"1":"0");}catch{}
}
try{setJournalCollapsed(localStorage.getItem("worldgen.journalCollapsed")==="1");}catch{setJournalCollapsed(false);}
journalToggle.addEventListener("click",()=>setJournalCollapsed(!journal.classList.contains("is-collapsed")));
document.getElementById("sphere-land-toggle").addEventListener("click", async event => {
  const land = selectedLand();
  if (!land) return;
  const button = event.currentTarget;
  const status = document.getElementById("sphere-land-status");
  button.disabled = true;
  if (land.usage > 0) lastLandUsage.set(land.id, land.usage);
  const usage = land.usage > 0 ? 0 : lastLandUsage.get(land.id) ?? 1;
  try {
    const response = await fetch(`/api/sphere/land-use/${encodeURIComponent(land.id)}?usage=${usage}`, {method:"POST"});
    const body = await response.json();
    if (!response.ok) throw new Error(body.error ?? `HTTP ${response.status}`);
    await refreshSphereData();
    status.textContent = `Использование: ${Math.round(usage * 100)}%. Влияние пересчитано, версия ${body.revision}; охвачено ${body.influencedCells.toLocaleString("ru-RU")} зон. Это диагностическое изменение, не экономическая команда.`;
  } catch (error) {
    status.textContent = error.message;
  } finally { button.disabled = false; }
});
new ResizeObserver(resize).observe(canvas.parentElement);

async function initialize() {
const [metadataResponse, previewResponse, atlasResponse] = await Promise.all([
  fetch("/api/sphere", {cache:"no-store"}),
  fetch("/api/sphere/preview?stride=4", {cache:"no-store"}),
  fetch("/assets/topographic-symbols.json")
]);
if (!metadataResponse.ok || !previewResponse.ok || !atlasResponse.ok) throw new Error("Не удалось загрузить сферический прототип");
metadata = await metadataResponse.json();
preview = await previewResponse.json();
if(!metadata.worldId||metadata.worldId!==preview.worldId)throw new Error("Сервер обновлён во время загрузки. Обновите страницу.");
mapData=new SphereMapData({worldId:metadata.worldId,faceSize:metadata.faceSize,chunkSize:metadata.chunkSize,faces:metadata.faces});
try{
  globeRenderer.initialize({preview,seaLevel:metadata.seaLevelMeters,seed:metadata.seed,faceSize:metadata.faceSize});
  void globeRenderer.loadStarTexture('/assets/2k_stars_milky_way.jpg').then(()=>scheduleRender());
  void globeRenderer.loadExactSurface(`/api/sphere/gpu-surface?worldId=${encodeURIComponent(metadata.worldId)}`).then(loaded=>{
    if(loaded)document.getElementById("sphere-gpu-status").textContent=`WebGL2 · точная поверхность ${metadata.faceSize}² × 6`;
    scheduleRender();
  }).catch(error=>console.warn("Exact GPU surface unavailable",error));
}
catch(error){globeRenderer.available=false;globeRenderer.error=error;console.warn("WebGL2 globe disabled",error);}
document.getElementById("sphere-gpu-status").textContent=globeRenderer.available?`WebGL2 · ${metadata.faceSize}² × 6`:"Canvas fallback";
atlas = await atlasResponse.json();
drawSymbol = createSymbolRenderer(atlas);
const legend=document.getElementById("sphere-map-legend");
for(const id of ["conifer","bare_tree","fruit_tree","berry_bush","grain","nut_tree","snow","wetland","river","contour","house","well","field","chicken","cow","game","resource_camp","trail","construction","ruin","boundary"]) {
  const symbol=atlas.symbols.find(item=>item.id===id);
  const entry=document.createElement("span");
  entry.append(symbolSvg(atlas,symbol),document.createTextNode(symbol.label));legend.append(entry);
}
for(const key of Object.keys(mapOptions)) document.getElementById(`sphere-show-${key}`).addEventListener("change",event=>{
  mapOptions[key]=event.target.checked;hideTooltip();scheduleRender();
});
faces = new Map(preview.faces.map(face => [face.face, face]));
chunkAxis = metadata.chunksPerFaceAxis;
faceIds = new Map(metadata.faces.map((face, index) => [face, index]));
document.getElementById("sphere-name").textContent = `${metadata.name} · ${metadata.faceSize} × ${metadata.faceSize} × 6 граней`;
document.getElementById("sphere-zones").textContent = metadata.zones.toLocaleString("ru-RU");
document.getElementById("sphere-triangles").textContent = metadata.triangles.toLocaleString("ru-RU");
document.getElementById("sphere-chunks").textContent = metadata.chunks.toLocaleString("ru-RU");
document.getElementById("sphere-lod").textContent = `1:${preview.stride}`;
document.getElementById("sphere-memory").textContent = `${metadata.estimatedTerrainMiB.toFixed(1)} МиБ`;
document.getElementById("sphere-sea-level").textContent = String(metadata.seaLevelMeters);
const settlementSelect = document.getElementById("sphere-settlement");
for (const settlement of metadata.settlements) {
  const option = document.createElement("option");
  option.value = settlement.id; option.textContent = settlement.name; settlementSelect.append(option);
}
settlementSelect.addEventListener("change", () => {
  const anchor = metadata.settlements.find(item => item.id === settlementSelect.value)?.buildings[0];
  if (!anchor) return;
  cancelZoomMotion();camera.focus(facePoint(anchor.face, anchor.x, anchor.y), 24);
  hideTooltip(); scheduleRender();scheduleScopeRefresh();
});
const parcelSelect = document.getElementById("sphere-parcel");
refreshParcelOptions();
parcelSelect.addEventListener("change", () => {
  if (parcelSelect.value === "") return;
  const parcel = parcels[Number(parcelSelect.value)];
  selectedCell = { face: parcel.face, x: parcel.x, y: parcel.y };
  cancelZoomMotion();camera.focus(facePoint(parcel.face, parcel.x, parcel.y), 48);
  hideTooltip(); refreshSelection(); scheduleRender();scheduleScopeRefresh();
});
resize();
void loadHydrology();
simulationController=await connectSphereSimulation({onState:applySimulationView,biosphere:metadata.biosphere,processes:metadata.processes??[],activities:metadata.activities??[],mapQuery:()=>mapData.query(),getScope:currentViewScope,
  scheduleUi:work=>scheduleInterfaceWork("simulation",work,90),
  onFocus:site=>{selectedCell={face:site.face,x:site.x,y:site.y};cancelZoomMotion();camera.focus(facePoint(site.face,site.x,site.y),48);hideTooltip();refreshSelection();scheduleRender();scheduleScopeRefresh();}});
scheduleScopeRefresh(0);
}
initialize().catch(error => {
  document.getElementById("sphere-view-error").textContent = `${error.message}. Перезагрузите страницу после восстановления сервера.`;
});

async function loadHydrology() {
  const status=document.getElementById("sphere-hydrology-status");
  status.textContent="Расчёт речной сети…";
  try {
    const response=await fetch("/api/sphere/hydrology");
    if(!response.ok) throw new Error(`HTTP ${response.status}`);
    const next=await response.json();
    if(next.worldId&&next.worldId!==metadata.worldId)throw new Error("Мир на сервере заменён. Обновите страницу");
    for(const reach of next.reaches)reach.displayPoints=roundSpherePath(reach.points);
    const hydroFaces=new Map(next.faces.map(face=>[face.face,face]));
    const sampler=createLakeSurfaceSampler({faceSize:metadata.faceSize,resolution:next.resolution,
      readDepth:(face,x,y)=>hydroFaces.get(face).lakeDepth[y*next.resolution+x],
      readShore:next.faces.every(face=>face.lakeShore)?(face,x,y)=>hydroFaces.get(face).lakeShore[y*next.resolution+x]:undefined});
    // Publish one complete data revision, never new shores with the old sampler.
    hydrology=next;sampleLakeSurface=sampler;
    status.textContent=`Речная сеть: ${hydrology.reaches.length} участков · расчёт стока 1:${hydrology.stride}, без сезонности и эрозии.`;
    const select=document.getElementById("sphere-nature");
    select.replaceChildren(select.options[0]);
    const ranked=[...hydrology.reaches].sort((a,b)=>b.points.length-a.points.length).slice(0,8);
    for(const [index,reach] of ranked.entries()) {
      const option=document.createElement("option");option.value=String(reach.id);
      option.textContent=`Речной бассейн ${index+1}`;select.append(option);
    }
    select.disabled=false;
    document.getElementById("sphere-hydrology-retry").hidden=true;
    scheduleRender();
  } catch(error) {
    status.textContent=`Реки пока не загружены: ${error.message}. Рельеф остаётся доступен.`;
    document.getElementById("sphere-hydrology-retry").hidden=false;
  }
}
document.getElementById("sphere-hydrology-retry").addEventListener("click",loadHydrology);
document.getElementById("sphere-nature").addEventListener("change",event=>{
  if(!hydrology || event.target.value==="") return;
  const reach=hydrology.reaches.find(item=>item.id===Number(event.target.value));
  if(!reach) return;
  const point=reach.points[Math.floor(reach.points.length*.55)];
  cancelZoomMotion();camera.focus({x:point[0],y:point[1],z:point[2]},8);
  hideTooltip();scheduleRender();
});

window.addEventListener("focus", async () => {
  if (!metadata || refreshPromise) return;
  try {
    await refreshSphereData();
  } catch { /* Existing terrain stays usable while the server is unavailable. */ }
});
