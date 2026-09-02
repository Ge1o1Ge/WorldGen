import { facePoint, contourSegments, joinSegments, symbolSpacing, symbolAnchor } from "./sphere-cartography.js";
import {waterShoreByte} from './sphere-water.js';

// Camera-independent data cache: only source revision and explicit LOD are keys.
export class WorldGeometryCache {
  constructor(capacity=256) { this.capacity=capacity;this.entries=new Map();this.builds=0; }
  get(key,version,build) {
    let entry=this.entries.get(key);
    if(!entry||entry.version!==version){entry={version,value:build()};this.builds++;}
    this.entries.delete(key);this.entries.set(key,entry);
    while(this.entries.size>this.capacity)this.entries.delete(this.entries.keys().next().value);
    return entry.value;
  }
}
export function roundWorldPath(path,subdivisions=4) {
  if(path.length<3)return path;
  const closed=path[0][0]===path.at(-1)[0]&&path[0][1]===path.at(-1)[1];
  const vertices=closed?path.slice(0,-1):path,result=[];
  const middle=(a,b)=>[(a[0]+b[0])/2,(a[1]+b[1])/2];
  if(!closed)result.push(vertices[0]);
  for(let i=closed?0:1;i<(closed?vertices.length:vertices.length-1);i++) {
    const b=vertices[i],a=middle(vertices[(i+vertices.length-1)%vertices.length],b),c=middle(b,vertices[(i+1)%vertices.length]);
    for(let step=0;step<=subdivisions;step++) {
      const t=step/subdivisions,s=1-t;
      result.push([a[0]*s*s+2*b[0]*s*t+c[0]*t*t,a[1]*s*s+2*b[1]*s*t+c[1]*t*t]);
    }
  }
  result.push(closed?result[0]:vertices.at(-1));return result;
}
export function clipSegmentToBox(a,b,box) {
  let enter=0,leave=1;const dx=b[0]-a[0],dy=b[1]-a[1];
  for(const [p,q]of[[-dx,a[0]-box.x0],[dx,box.x1-a[0]],[-dy,a[1]-box.y0],[dy,box.y1-a[1]]]) {
    if(p===0){if(q<0)return null;continue;}
    const t=q/p;if(p<0)enter=Math.max(enter,t);else leave=Math.min(leave,t);
    if(enter>leave)return null;
  }
  return [[a[0]+dx*enter,a[1]+dy*enter],[a[0]+dx*leave,a[1]+dy*leave]];
}
export function clipWorldPath(path,box) {
  const runs=[];let run=[];
  for(let i=1;i<path.length;i++) {
    const segment=clipSegmentToBox(path[i-1],path[i],box);
    if(!segment){if(run.length>1)runs.push(run);run=[];continue;}
    if(run.length&&Math.hypot(run.at(-1)[0]-segment[0][0],run.at(-1)[1]-segment[0][1])>1e-7){runs.push(run);run=[];}
    if(!run.length)run.push(segment[0]);run.push(segment[1]);
  }
  if(run.length>1)runs.push(run);return runs;
}
export function buildWorldTile({face,tx,ty,size,tileSize=32,step=1,sample,cityCount,seaLevel,water=true,kinds=["contour","coast","forest","boundary"]}) {
  const x0=tx*tileSize-.5,y0=ty*tileSize-.5;
  const box={x0,y0,x1:Math.min(size-.5,x0+tileSize),y1:Math.min(size-.5,y0+tileSize)};
  // Two-node halo: neighboring tiles round the same curve, then clip to their core.
  const originX=x0-2*step,originY=y0-2*step;
  const columns=Math.round((box.x1-x0)/step)+5,rows=Math.round((box.y1-y0)/step)+5;
  const elevation=new Float32Array(columns*rows),depth=new Float32Array(columns*rows),forest=new Float32Array(columns*rows);
  const owners=Array.from({length:kinds.includes("boundary")?cityCount:0},()=>new Float32Array(columns*rows));let min=Infinity,max=-Infinity;
  for(let y=0;y<rows;y++)for(let x=0;x<columns;x++) {
    const s=sample({face,x:originX+x*step,y:originY+y*step},owners.length>0),i=y*columns+x;
    elevation[i]=s.elevation;forest[i]=s.forest;depth[i]=water?(s.lakeShore??(s.lakeDepth??0)-1):-1;
    min=Math.min(min,s.elevation);max=Math.max(max,s.elevation);
    owners.forEach((values,owner)=>values[i]=s.claims.get(owner)??0);
  }
  const paths=[];
  function add(field,threshold,kind) {
    for(const line of joinSegments(contourSegments(field,columns,rows,threshold,step))) {
      const world=line.map(([x,y])=>[x+originX,y+originY]);
      const rounded=kind==="coast"?world:roundWorldPath(world);
      for(const clipped of clipWorldPath(rounded,box)) {
        const points=clipped.map(([x,y])=>facePoint(face,x,y,size));
        const label=kind==="contour"&&clipped.length>=40?points[Math.floor(points.length/2)]:null;
        paths.push({kind,value:threshold,points,label});
      }
    }
  }
  if(kinds.includes("contour"))for(let value=Math.ceil(min/10)*10;value<=max;value+=10)add(elevation,value,"contour");
  if(kinds.includes("coast")){
    // The fill texture is sampled at microcell centres (integer coordinates).
    // Trace the identical quantised field here; sampling at half coordinates
    // and rounding it again visibly moved sharp bays and narrow lakes.
    const coastOriginX=tx*tileSize-2,coastOriginY=ty*tileSize-2;
    const coastColumns=Math.round((box.x1-x0)/step)+5,coastRows=Math.round((box.y1-y0)/step)+5;
    const coast=new Float32Array(coastColumns*coastRows);
    for(let y=0;y<coastRows;y++)for(let x=0;x<coastColumns;x++){
      const s=sample({face,x:coastOriginX+x*step,y:coastOriginY+y*step});
      coast[y*coastColumns+x]=water?waterShoreByte(s.lakeShore??(s.lakeDepth??0)-1)-128:-128;
    }
    for(const line of joinSegments(contourSegments(coast,coastColumns,coastRows,0,step))){
      const world=line.map(([x,y])=>[x+coastOriginX,y+coastOriginY]);
      for(const clipped of clipWorldPath(world,box))paths.push({kind:"coast",value:0,points:clipped.map(([x,y])=>facePoint(face,x,y,size)),label:null});
    }
  }
  if(kinds.includes("forest"))add(forest,.42,"forest");
  for(const values of owners)add(values,.5,"boundary");return paths;
}
export function worldTileSymbols({face,tx,ty,size,tileSize=32,zoom,seed,sample,settlements,seaLevel,biosphere}) {
  const spacing=symbolSpacing(zoom),symbols=[];
  for(let gy=Math.floor(ty*tileSize/spacing);gy<Math.ceil(Math.min(size,(ty+1)*tileSize)/spacing);gy++)
  for(let gx=Math.floor(tx*tileSize/spacing);gx<Math.ceil(Math.min(size,(tx+1)*tileSize)/spacing);gx++) {
    const anchor=symbolAnchor(face,gx,gy,spacing,seed);
    if(anchor.x<-.5||anchor.y<-.5||anchor.x>=size-.5||anchor.y>=size-.5)continue;
    const occupied=settlements.some(city=>[...city.buildings,...city.usedLands.filter(land=>land.usage>0)].some(asset=>
      asset.face===face&&Math.hypot(asset.x-anchor.x,asset.y-anchor.y)<Math.max(.85,spacing*.65)));
    if(occupied)continue;
    const s=sample(anchor);if(s.lakeDepth>1)continue;
    let kind=s.biome===5?"wetland":s.forest>.42?(anchor.variant<.45?"conifer":"deciduous"):
      s.biome===6?"rock":anchor.variant>.85&&s.biome===3?"grass":null;
    const point=facePoint(face,anchor.x,anchor.y,size);
    if(biosphere&&zoom>=8)kind=biologicalGlyph(kind,wildPlants(biosphere,seed,point,s),anchor.variant);
    if(kind)symbols.push({...anchor,kind,point});
  }
  return symbols;
}
export function landMaskAlpha(elevation,lakeDepth,seaLevel,showWater=true) {
  // Elevation below datum is terrain, not water. The signed dynamic water
  // field is the sole wet/dry authority after world generation.
  return showWater&&lakeDepth>1?0:255;
}

export function roundSpherePath(points) {
  if(points.length<3)return points;
  const result=[points[0]],mid=(a,b)=>a.map((value,i)=>(value+b[i])/2);
  for(let i=1;i<points.length-1;i++) {
    const a=mid(points[i-1],points[i]),b=points[i],c=mid(points[i],points[i+1]);
    for(let j=0;j<=4;j++) {
      const t=j/4,s=1-t,p=b.map((value,k)=>a[k]*s*s+2*value*s*t+c[k]*t*t),length=Math.hypot(...p);
      result.push(p.map(value=>value/length));
    }
  }
  result.push(points.at(-1));return result;
}
import {wildPlants,biologicalGlyph} from './sphere-biology.js';
