import { facePoint, contourInterval } from "./sphere-cartography.js";
import { WorldGeometryCache, buildWorldTile, worldTileSymbols } from "./sphere-world-geometry.js";
import { buildingGlyph,buildingAnchor } from "./settlement-symbols.js";
import {TrailGeometryCache,trailKey} from "./sphere-trails.js";
import {wildlifeZonePoints} from "./sphere-wildlife.js";
import {FieldGeometryCache,drawFieldGroups,groupedField} from "./sphere-fields.js";
import {winterSymbol} from './sphere-weather.js';
const geometry=new WorldGeometryCache(1400),landGeometry=new WorldGeometryCache(1400),symbolCache=new WorldGeometryCache(384);
const trailGeometry=new TrailGeometryCache();
const fieldGeometry=new FieldGeometryCache();

export function drawCartographicLayer({context:ctx,riverContext,width,height,zoom,metadata,atlas,
  sampleWorld,projectCell,projectVector,hydrology,drawSymbol,options,layer,tiles,dataVersion,staticVersion,simulation,sampleWeather}) {
  const palette=atlas.palette,topographic=layer==="topographic",interval=contourInterval(zoom),labels=[];
  const project=point=>projectVector([point.x,point.y,point.z]);
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
  if(topographic&&options.water&&hydrology)for(const reach of hydrology.reaches) {
    if(zoom<1.8&&reach.runoff<180)continue;
    stroke(riverContext,(reach.displayPoints??reach.points).map(p=>({x:p[0],y:p[1],z:p[2]})),palette.water,
      Math.min(4.5,.75+Math.log2(1+reach.runoff/55)*.48+Math.max(0,Math.log2(zoom))*.13));
  }
  for(const tile of tiles) {
    const step=zoom>=3.5?1:4,key=`${tile.face}:${tile.tx}:${tile.ty}:${step}:${options.water}`;
    const parameters={...tile,size:metadata.faceSize,tileSize:metadata.chunkSize,step,sample:sampleWorld,
      cityCount:metadata.settlements.length,seaLevel:metadata.seaLevelMeters,water:options.water};
    // The shoreline is already drawn from the same raster field as the fill and
    // land mask. A separately rounded/coarsened vector contour would drift.
    const paths=[...geometry.get(key,staticVersion(tile),()=>buildWorldTile({...parameters,kinds:["contour"]})),
      ...landGeometry.get(key,dataVersion(tile),()=>buildWorldTile({...parameters,kinds:["forest","boundary"]}))];
    for(const path of paths) {
      if(path.kind==="contour"&&options.contours&&path.value%interval===0&&(topographic||layer==="elevation")) {
        const major=path.value%(interval*5)===0;
        stroke(riverContext,path.points,palette.contour,major?1.25:.65,[],major?.76:.42);
        if(major&&path.label&&zoom>=1.8)labels.push({point:path.label,text:String(path.value)});
      }else if(path.kind==="forest"&&topographic)stroke(riverContext,path.points,palette.forest,.85,[1,3],.6);
      else if(path.kind==="boundary"&&options.boundaries&&(topographic||layer==="political"))stroke(ctx,path.points,palette.boundary,1.4,[7,3,1.5,3],.95);
    }
    if(topographic&&options.symbols&&zoom>=1.8) {
      const band=zoom<2?0:zoom<4?1:zoom<8?2:zoom<16?3:zoom<32?4:5;
      const symbols=symbolCache.get(`${tile.face}:${tile.tx}:${tile.ty}:${band}:${options.water}`,dataVersion(tile),
        ()=>worldTileSymbols({...tile,size:metadata.faceSize,tileSize:metadata.chunkSize,zoom,seed:metadata.seed,
          sample:sampleWorld,settlements:metadata.settlements,seaLevel:metadata.seaLevelMeters,biosphere:metadata.biosphere}));
      for(const symbol of symbols){const p=project(symbol.point);if(!visible(p))continue;
        const glyph=options.winter?winterSymbol(symbol.kind,sampleWeather?.(symbol.point)):symbol.kind;
        drawSymbol(ctx,glyph,p.x,p.y,symbol.kind==="grass"?15:19,Math.min(1,Math.max(0,p.z/.3))*(symbol.kind==="grass"?.6:.82));}
    }
  }
  if(topographic&&zoom>=8){
    const trails=simulation?.trails??[],strengths=new Map(trails.map(edge=>[trailKey(edge),edge.strength]));
    for(const path of trailGeometry.get(trails,metadata.faceSize)){
      const strength=strengths.get(path.key);
      stroke(riverContext,path.points,palette.ink,.55+strength*.9,strength<.35?[2,3]:[],
        Math.min(1,strength/.12)*(.2+strength*.6));
    }
  }
  if(topographic&&zoom>=8)drawFieldGroups(ctx,fieldGeometry.get(metadata.settlements,metadata.faceSize),project,{symbols:options.symbols&&!(metadata.biosphere&&zoom>=16),drawSymbol});
  if(topographic&&options.symbols&&zoom>=8)for(const settlement of metadata.settlements) {
    for(const land of settlement.usedLands){const p=projectCell(land);if(land.usage<=0||!visible(p))continue;
      const herdSpecies=land.id?.startsWith('pasture:')?metadata.biosphere?.animals.find(a=>a.id===land.id.split(':').at(-1)):null;
      drawSymbol(ctx,herdSpecies?.symbol??(land.kind==="Orchard"?"orchard":land.kind==="Pasture"?"grass":"field"),p.x,p.y,22);}
    const buildings=new Map();
    for(const building of settlement.buildings){if(groupedField(building))continue;if(!buildings.has(building.id))buildings.set(building.id,[]);buildings.get(building.id).push(building);}
    for(const footprint of buildings.values()) {
      const vectors=footprint.map(buildingAnchor).map(cell=>facePoint(cell.face,cell.x,cell.y,metadata.faceSize));
      const center=vectors.reduce((sum,p)=>[sum[0]+p.x,sum[1]+p.y,sum[2]+p.z],[0,0,0]),length=Math.hypot(...center);
      const p=projectVector(center.map(value=>value/length));if(!visible(p))continue;
      const building=footprint[0],small=building.slot>=0;
      drawSymbol(ctx,buildingGlyph(building),p.x,p.y,small?Math.min(17,Math.max(9,zoom*.3)):23,building.status==="abandoned"?.5:1);
    }
  }
  const labelContext=riverContext;
  if(options.wildlife&&zoom>=8)for(const group of simulation?.wildlife??[]){
    const p=projectCell(group);if(!visible(p))continue;
    const alert=group.alert>=.05;
    stroke(riverContext,wildlifeZonePoints(group,metadata.faceSize),alert?"#a25436":palette.contour,1,[3,4],.8);
    drawSymbol(riverContext,metadata.biosphere?.animals.find(a=>a.id===group.speciesId)?.symbol??"game",p.x,p.y,20,alert?1:.75);
  }
  if(topographic&&options.symbols&&zoom>=16)for(const plot of simulation?.biologyPlots??[]){const p=projectCell(plot);if(visible(p)){const crop=metadata.biosphere?.crops.find(c=>c.id===plot.cropId);if(crop)drawSymbol(ctx,crop.symbol,p.x,p.y,19,.9);}}
  if(topographic&&options.symbols&&zoom>=8)for(const camp of simulation?.resourceCamps??[]){const p=projectCell(camp);if(visible(p)&&!camp.abandoned)drawSymbol(ctx,'resource_camp',p.x,p.y,21,camp.work>=metadata.biosphere.campSetupHours?1:.5);}
  labelContext.save();labelContext.font="11px Georgia, serif";labelContext.textAlign="center";labelContext.textBaseline="middle";
  for(const label of labels){const p=project(label.point);if(!visible(p)||p.z<.2)continue;
    labelContext.strokeStyle=palette.paper;labelContext.lineWidth=3.5;labelContext.strokeText(label.text,p.x,p.y);labelContext.fillStyle=palette.contour;labelContext.fillText(label.text,p.x,p.y);}
  labelContext.restore();return {builds:geometry.builds,landBuilds:landGeometry.builds,symbolBuilds:symbolCache.builds};
}
