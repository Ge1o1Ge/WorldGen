import { facePoint, locateFace, contourInterval } from "./sphere-cartography.js?v=lod3";
import { WorldGeometryCache, buildWorldTile, worldTileSymbols, roundSpherePath } from "./sphere-world-geometry.js";
import { buildingGlyph,buildingAnchor } from "./settlement-symbols.js";
import {TrailGeometryCache,trailKey} from "./sphere-trails.js";
import {FieldGeometryCache,drawFieldGroups,groupedField} from "./sphere-fields.js";
import {winterSymbol} from './sphere-weather.js';
const geometry=new WorldGeometryCache(1400),landGeometry=new WorldGeometryCache(1400),symbolCache=new WorldGeometryCache(384);
const trailGeometry=new TrailGeometryCache();
const fieldGeometry=new FieldGeometryCache();

export const LOCAL_CARTOGRAPHY_ZOOM=16;
export const OVERVIEW_SYMBOL_ZOOM=1.8;
// Cross-fading two independently rebuilt river paths changes apparent colour
// and width. Keep revisions atomic until the renderer can interpolate matching
// vertices of one path instead of blending two complete meshes.
export const RIVER_CROSSFADE=false;

// At regional zoom the exact terrain texture is already in WebGL, while a
// one-cell vector mesh adds little visible detail and is much more expensive
// to rebuild. Reserve it for the local view where individual parcels matter.
export function cartographyStep(zoom){return zoom>=LOCAL_CARTOGRAPHY_ZOOM?1:4;}
export function riverClassVisibleAtZoom(riverClass,zoom){return riverClass!=="small"||zoom>=3;}

function normalizedMix(a,b,t){
  const p={x:a.x+(b.x-a.x)*t,y:a.y+(b.y-a.y)*t,z:a.z+(b.z-a.z)*t},length=Math.hypot(p.x,p.y,p.z);
  return {x:p.x/length,y:p.y/length,z:p.z/length};
}

export function splitSpherePath(points,predicate,wanted=true,iterations=10){
  const runs=[];let run=[];
  const boundary=(a,b,aState)=>{
    let left=a,right=b;
    for(let index=0;index<iterations;index++){
      const middle=normalizedMix(left,right,.5);
      if(predicate(middle)===aState)left=middle;else right=middle;
    }
    return normalizedMix(left,right,.5);
  };
  let a=points[0],aState=a?predicate(a):false;
  if(a&&aState===wanted)run=[a];
  for(let index=1;index<points.length;index++){
    const b=points[index],bState=predicate(b);
    if(aState===bState){if(bState===wanted){if(!run.length)run.push(a);run.push(b);}}
    else{
      const edge=boundary(a,b,aState);
      if(aState===wanted){run.push(edge);if(run.length>1)runs.push(run);run=[];}
      else run=[edge,b];
    }
    a=b;aState=bState;
  }
  if(run.length>1)runs.push(run);return runs;
}

const pointObject=point=>Array.isArray(point)?{x:point[0],y:point[1],z:point[2]}:point;

export function spherePathVisible(points,project,width,height,margin=48){
  const projected=points.map(pointObject).map(project).filter(Boolean);
  if(!projected.length)return false;
  return !(projected.every(p=>p.x< -margin)||projected.every(p=>p.x>width+margin)||
    projected.every(p=>p.y< -margin)||projected.every(p=>p.y>height+margin));
}

function probeSpherePath(points,steps=4){
  if(points.length<2)return points;
  const result=[points[0]];
  for(let index=1;index<points.length;index++){
    const a=points[index-1],b=points[index];
    for(let step=1;step<=steps;step++)result.push(normalizedMix(a,b,step/steps));
  }
  return result;
}

// Clip the cell-centre route before smoothing. A quadratic curve can bow back
// across a lake near a sharp shore; clipping only afterwards turns that detour
// into several short blue dashes. If a smoothed run re-enters water, retain the
// original (still shoreline-clipped) run for this rare junction.
export function riverLandRuns(points,waterAtPoint){
  // A reach joins cell centres. Both ends can be dry while the arc between
  // them crosses a one-cell pond, so endpoints alone are not a sufficient
  // water mask. Four spherical probes keep that narrow crossing out of the
  // retained GL quad without tying the result to the current camera.
  const raw=probeSpherePath(points.map(pointObject)),runs=splitSpherePath(raw,waterAtPoint,false);
  return runs.flatMap(run=>{
    if(run.length<3)return [run];
    const smooth=roundSpherePath(run.map(point=>[point.x,point.y,point.z])).map(pointObject);
    const clipped=splitSpherePath(probeSpherePath(smooth),waterAtPoint,false);
    if(clipped.length===0)return [];
    return clipped.length===1?clipped:[run];
  });
}

export function dynamicRiverLines(reach,runs,zoom,color){
  const discharge=reach.dischargeM3PerDay??reach.runoff??0,riverClass=reach.channelClass??"small";
  if(riverClass==="medium"){
    const gap=1.15+Math.min(.8,Math.max(0,Math.log2(zoom/8))*.18);
    return runs.flatMap(points=>[
      {points,color,width:1.05,offset:-gap,alpha:1},
      {points,color,width:1.05,offset:gap,alpha:1}
    ]);
  }
  if(riverClass==="major"){
    const physicalWidth=Number(reach.widthMeters??50);
    return runs.map(points=>({points,color,width:Math.min(18,5+physicalWidth/8+Math.max(0,Math.log2(zoom/8))*.45),alpha:1}));
  }
  const width=Math.min(1.55,.82+Math.log2(1+discharge/25)*.13);
  return runs.map(points=>({points,color,width,alpha:1}));
}

export function riverDisplayRun(points){
  if(points.length<3)return points.map(pointObject);
  return roundSpherePath(points.map(point=>{
    const p=pointObject(point);return [p.x,p.y,p.z];
  })).map(pointObject);
}

export function drawCartographicLayer({context:ctx,riverContext,width,height,zoom,metadata,atlas,
  sampleWorld,projectCell,projectVector,hydrology,drawSymbol,options,layer,tiles,dataVersion,staticVersion,simulation,sampleWeather,lineLayer,symbolLayer,selection,incremental=false,gpuContours=false}) {
  const palette=atlas.palette,topographic=layer==="topographic",interval=contourInterval(zoom),labels=[];
  const detailScale=zoom<8?1:zoom<16?1.08:zoom<32?1.2:1.34;
  const lineKeys=[],symbolKeys=[];
  const project=point=>projectVector([point.x,point.y,point.z]);
  const waterAtPoint=point=>{
    const location=locateFace(point);
    const sample=sampleWorld({face:location.face,x:(location.u+1)*metadata.faceSize/2-.5,y:(location.v+1)*metadata.faceSize/2-.5});
    return sample.lakeDepth>1;
  };
  const visible=p=>p&&p.x>=-24&&p.y>=-24&&p.x<=width+24&&p.y<=height+24;
  function stroke(context,points,color,lineWidth,dash=[],alpha=1) {
    const projected=points.map(project);
    if(projected.every(p=>!p||p.x<0)||projected.every(p=>!p||p.x>width)||
      projected.every(p=>!p||p.y<0)||projected.every(p=>!p||p.y>height))return;
    context.save();context.beginPath();let drawing=false;
    for(const p of projected){if(!p){drawing=false;continue;}if(drawing)context.lineTo(p.x,p.y);else context.moveTo(p.x,p.y);drawing=true;}
    context.strokeStyle=color;context.lineWidth=lineWidth;context.lineJoin="round";context.lineCap="round";
    context.globalAlpha=alpha;context.setLineDash(dash);context.stroke();context.restore();
  }
  if(topographic&&options.water&&hydrology){
    if(lineLayer?.available){
      const riverBand=zoom<3?0:1,riverVersion=`${hydrology.generator}:${hydrology.revision??0}:${riverBand}`;
      for(const reach of hydrology.reaches){
        if(!riverClassVisibleAtZoom(reach.channelClass,zoom))continue;
        if(!spherePathVisible(reach.points,project,width,height))continue;
        const riverKey=`river:${reach.id}`;
        lineLayer.retain(riverKey,riverVersion,()=>dynamicRiverLines(reach,[riverDisplayRun(reach.points)],zoom,palette.water),{animate:RIVER_CROSSFADE});
        lineKeys.push(riverKey);
      }
    }else for(const reach of hydrology.reaches){
      if(!riverClassVisibleAtZoom(reach.channelClass,zoom))continue;
      if(!spherePathVisible(reach.points,project,width,height))continue;
      for(const line of dynamicRiverLines(reach,[riverDisplayRun(reach.points)],zoom,palette.water))
        stroke(riverContext,line.points,line.color,reach.channelClass==="medium"?2.4:line.width);
    }
  }
  for(const tile of tiles) {
    const step=cartographyStep(zoom),key=`${tile.face}:${tile.tx}:${tile.ty}:${step}:${options.water}`;
    const parameters={...tile,size:metadata.faceSize,tileSize:metadata.chunkSize,step,sample:sampleWorld,
      cityCount:metadata.settlements.length,seaLevel:metadata.seaLevelMeters,water:options.water};
    // WebGL derives its shoreline analytically from the same signed field as
    // the fill. Keep vector coasts only for the Canvas fallback.
    const contourPaths=gpuContours?[]:geometry.get(key,staticVersion(tile),()=>buildWorldTile({...parameters,kinds:lineLayer?.available?["contour"]:["contour","coast"]}));
    const paths=[...contourPaths,...landGeometry.get(key,dataVersion(tile),()=>buildWorldTile({...parameters,kinds:["forest","boundary"]}))];
    if(!gpuContours&&lineLayer?.available&&(options.contours||options.water)&&(topographic||layer==="elevation")){
      const lineKey=`contour:${key}:${interval}:${options.contours}:${options.water}`,version=staticVersion(tile);
      lineLayer.retain(lineKey,version,()=>{
        const result=[];
        for(const path of contourPaths){
          if(path.kind==="coast"){
            if(options.water)result.push({points:path.points,color:palette.water,width:1.15,alpha:.9});
            continue;
          }
          if(!options.contours)continue;
          if(path.value%interval!==0)continue;
          const major=path.value%(interval*5)===0,isobath=path.value<metadata.seaLevelMeters;
          const style=isobath
            ?{color:palette.water,width:major?.95:.58,alpha:major?.42:.22}
            :{color:palette.contour,width:major?1.25:.65,alpha:major?.76:.42};
          for(const points of splitSpherePath(path.points,waterAtPoint,isobath))result.push({points,...style});
        }
        return result;
      });
      lineKeys.push(lineKey);
    }
    if(lineLayer?.available&&(topographic||layer==="political")){
      const landKey=`land-lines:${tile.face}:${tile.tx}:${tile.ty}:${step}:${options.boundaries}`,version=dataVersion(tile);
      lineLayer.retain(landKey,version,()=>paths.flatMap(path=>path.kind==="forest"&&topographic
        ?[{points:path.points,color:palette.forest,width:.85*detailScale,alpha:.66,dash:[1,3]}]
        :path.kind==="boundary"&&options.boundaries?[{points:path.points,color:palette.boundary,width:1.4*detailScale,alpha:.98,dash:[7,3,1.5,3]}]:[]));
      lineKeys.push(landKey);
    }
    for(const path of paths) {
      if(path.kind==="contour"&&options.contours&&path.value%interval===0&&(topographic||layer==="elevation")) {
        const major=path.value%(interval*5)===0;
        if(!lineLayer?.available)stroke(riverContext,path.points,palette.contour,major?1.25:.65,[],major?.76:.42);
        if(major&&path.label&&zoom>=4)labels.push({point:path.label,text:String(path.value)});
      }else if(path.kind==="forest"&&topographic){if(!lineLayer?.available)stroke(riverContext,path.points,palette.forest,.85,[1,3],.6);}
      else if(path.kind==="boundary"&&options.boundaries&&(topographic||layer==="political")){if(!lineLayer?.available)stroke(ctx,path.points,palette.boundary,1.4,[7,3,1.5,3],.95);}
    }
    if(topographic&&options.symbols&&zoom>=OVERVIEW_SYMBOL_ZOOM) {
      const band=zoom<2?0:zoom<4?1:zoom<8?2:zoom<16?3:zoom<32?4:5;
      const cacheKey=`${tile.face}:${tile.tx}:${tile.ty}:${band}:${options.water}`,version=dataVersion(tile);
      const symbols=symbolCache.get(cacheKey,version,()=>worldTileSymbols({...tile,size:metadata.faceSize,tileSize:metadata.chunkSize,zoom,seed:metadata.seed,
        sample:sampleWorld,settlements:metadata.settlements,seaLevel:metadata.seaLevelMeters,biosphere:metadata.biosphere}));
      if(symbolLayer?.ready){
        const gpuKey=`world-symbols:${cacheKey}:${options.winter}`;
        symbolLayer.retain(gpuKey,`${version}:${options.winter}`,()=>symbols.map(symbol=>({point:symbol.point,
          id:options.winter?winterSymbol(symbol.kind,sampleWeather?.(symbol.point)):symbol.kind,
          size:(symbol.kind==="grass"?15:19)*detailScale,opacity:symbol.kind==="grass"?.68:.92})));
        symbolKeys.push(gpuKey);
      }else for(const symbol of symbols){const p=project(symbol.point);if(!visible(p))continue;
        const glyph=options.winter?winterSymbol(symbol.kind,sampleWeather?.(symbol.point)):symbol.kind;
        drawSymbol(ctx,glyph,p.x,p.y,symbol.kind==="grass"?15:19,Math.min(1,Math.max(0,p.z/.3))*(symbol.kind==="grass"?.6:.82));}
    }
  }
  if(topographic&&zoom>=8){
    const trails=simulation?.trails??[],strengths=new Map(trails.map(edge=>[trailKey(edge),edge.strength]));
    const paths=trailGeometry.get(trails,metadata.faceSize);
    if(lineLayer?.available){
      const trailVersion=trails.map(edge=>`${trailKey(edge)}:${Math.round(edge.strength*20)}`).sort().join("|");
      const trailKeyName="trails";
      lineLayer.retain(trailKeyName,trailVersion,()=>paths.map(path=>{const strength=strengths.get(path.key);return{points:path.points,color:palette.ink,
        width:.55+strength*.9,dash:strength<.35?[2,3]:[],alpha:Math.min(1,strength/.12)*(.2+strength*.6)};}));
      lineKeys.push(trailKeyName);
    }else for(const path of paths){
      const strength=strengths.get(path.key);stroke(riverContext,path.points,palette.ink,.55+strength*.9,strength<.35?[2,3]:[],Math.min(1,strength/.12)*(.2+strength*.6));
    }
  }
  if(topographic&&zoom>=8){
    const groups=fieldGeometry.get(metadata.settlements,metadata.faceSize,simulation?.biologyPlots);
    if(lineLayer?.available){
      const fieldKey='field-boundaries',fieldVersion=fieldGeometry.builds;
      lineLayer.retain(fieldKey,fieldVersion,()=>groups.flatMap(group=>group.rings.map(points=>({points,
        color:group.landUse==='orchard'?'#5f7f55':'#95875d',width:.9*detailScale,alpha:.96}))));lineKeys.push(fieldKey);
    }else drawFieldGroups(ctx,groups,project,{symbols:false,drawSymbol});
    if(options.symbols&&symbolLayer?.ready&&!(metadata.biosphere&&zoom>=16)){
      const fieldSymbolKey='field-group-symbols';
      symbolLayer.retain(fieldSymbolKey,`${fieldGeometry.builds}:${detailScale}`,()=>groups.map(group=>({point:group.anchor,id:group.landUse==='orchard'?'orchard':'field',size:24*detailScale,opacity:group.landUse==='orchard'?.92:.88})));
      symbolKeys.push(fieldSymbolKey);
    }
  }
  const dynamicSymbols=[],parcelLines=[];
  if(topographic&&options.symbols&&zoom>=8)for(const settlement of metadata.settlements) {
    const occupied=new Map();
    for(const land of settlement.usedLands.filter(land=>land.usage>0)){
      const key=`${land.face??land.Face}:${land.x??land.X}:${land.y??land.Y}`;
      if(!occupied.has(key))occupied.set(key,[]);occupied.get(key).push(land);
    }
    for(const lands of occupied.values())for(const [index,land] of lands.entries()){
      const point=facePoint(land.face??land.Face,land.x??land.X,land.y??land.Y,metadata.faceSize);
      const herdSpecies=land.id?.startsWith('pasture:')?metadata.biosphere?.animals.find(a=>a.id===land.id.split(':').at(-1)):null;
      const offset=(index-(lands.length-1)/2)*9;
      // Several herds in one zone intentionally overlap only slightly. A later
      // atlas revision can add a proper clustered pasture glyph.
      if(herdSpecies){if(index===0)dynamicSymbols.push({point,id:'grass',size:23*detailScale,opacity:.58});dynamicSymbols.push({point,id:herdSpecies.symbol,size:18*detailScale,opacity:.98,offset});}
      else dynamicSymbols.push({point,id:land.kind==="Orchard"?'orchard':land.kind==="Pasture"?'grass':'field',size:22*detailScale,opacity:1,offset});
    }
    const buildings=new Map();
    for(const building of settlement.buildings){if(groupedField(building))continue;if(!buildings.has(building.id))buildings.set(building.id,[]);buildings.get(building.id).push(building);}
    for(const footprint of buildings.values()) {
      const vectors=footprint.map(buildingAnchor).map(cell=>facePoint(cell.face,cell.x,cell.y,metadata.faceSize));
      const center=vectors.reduce((sum,p)=>[sum[0]+p.x,sum[1]+p.y,sum[2]+p.z],[0,0,0]),length=Math.hypot(...center);
      const point=center.map(value=>value/length);
      const building=footprint[0],small=building.slot>=0;
      dynamicSymbols.push({point,id:buildingGlyph(building),size:small?Math.min(25,Math.max(11,zoom*.42)):23*detailScale,opacity:building.status==="abandoned"?.5:1});
    }
    const outlined=new Set();
    for(const cell of [...settlement.usedLands,...settlement.buildings]){
      const cellKey=`${cell.face}:${cell.x}:${cell.y}`;if(outlined.has(cellKey))continue;outlined.add(cellKey);
      const flooded=Number(cell.floodedDays)>0;
      if(cell.buildingTypeId==='garden'&&cell.status==='active'&&!flooded)continue;
      parcelLines.push({points:[[-.5,-.5],[.5,-.5],[.5,.5],[-.5,.5],[-.5,-.5]].map(([dx,dy])=>facePoint(cell.face,cell.x+dx,cell.y+dy,metadata.faceSize)),
        color:flooded?'#38a8bd':cell.usage>0?'#95824e':'#59655d',width:(flooded?1.35:.85)*detailScale,alpha:flooded?.96:.72,dash:cell.usage!==undefined?[4,3]:[]});
    }
  }
  if(topographic&&options.symbols&&zoom<8)for(const settlement of metadata.settlements){const anchor=settlement.anchor??settlement.buildings[0];if(anchor)dynamicSymbols.push({point:facePoint(anchor.face,anchor.x,anchor.y,metadata.faceSize),id:'camp',size:13,opacity:1});}
  const labelContext=riverContext;
  if(topographic&&options.symbols&&zoom>=16)for(const plot of simulation?.biologyPlots??[]){const crop=metadata.biosphere?.crops.find(c=>c.id===plot.cropId);if(crop)dynamicSymbols.push({point:facePoint(plot.face,plot.x,plot.y,metadata.faceSize),id:crop.symbol,size:19*detailScale,opacity:.94});}
  if(topographic&&options.symbols&&zoom>=8)for(const camp of simulation?.resourceCamps??[])if(!camp.abandoned)dynamicSymbols.push({point:facePoint(camp.face,camp.x,camp.y,metadata.faceSize),id:'resource_camp',size:21*detailScale,opacity:camp.work>=metadata.biosphere.campSetupHours?1:.55});
  if(dynamicSymbols.length){
    if(symbolLayer?.ready){
      const dynamicKey='settlement-symbols',dynamicVersion=dynamicSymbols.map(item=>`${item.id}:${item.point.join?.(',')??`${item.point.x},${item.point.y},${item.point.z}`}:${item.size}:${item.opacity}`).join('|');
      symbolLayer.retain(dynamicKey,dynamicVersion,()=>dynamicSymbols);symbolKeys.push(dynamicKey);
    }else for(const symbol of dynamicSymbols){const p=project(symbol.point);if(!visible(p))continue;
      drawSymbol(ctx,symbol.id,p.x+(symbol.offset??0),p.y,symbol.size,symbol.opacity);}
  }
  if(selection)parcelLines.push({points:[[-.5,-.5],[.5,-.5],[.5,.5],[-.5,.5],[-.5,-.5]].map(([dx,dy])=>facePoint(selection.face,selection.x+dx,selection.y+dy,metadata.faceSize)),color:'#64f0d1',width:2,alpha:1});
  if(parcelLines.length&&lineLayer?.available){const parcelKey='parcel-outlines',parcelVersion=parcelLines.map(line=>`${line.color}:${line.width}:${line.points[0].x},${line.points[0].y},${line.points[0].z}`).join(':');
    lineLayer.retain(parcelKey,parcelVersion,()=>parcelLines);lineKeys.push(parcelKey);}
  if(!symbolLayer?.ready){labelContext.save();labelContext.font="11px Georgia, serif";labelContext.textAlign="center";labelContext.textBaseline="middle";
    for(const label of labels){const p=project(label.point);if(!visible(p)||p.z<.2)continue;
      labelContext.strokeStyle=palette.paper;labelContext.lineWidth=3.5;labelContext.strokeText(label.text,p.x,p.y);labelContext.fillStyle=palette.contour;labelContext.fillText(label.text,p.x,p.y);}
    labelContext.restore();}
  lineLayer?.commit(lineKeys,{incremental});symbolLayer?.commit(symbolKeys,{incremental});
  return {builds:geometry.builds,landBuilds:landGeometry.builds,symbolBuilds:symbolCache.builds,
    lineBuilds:lineLayer?.builds??0,lineTiles:lineKeys.length,labels};
}
