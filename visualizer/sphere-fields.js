import {facePoint} from "./sphere-cartography.js";

const key=p=>[p.x,p.y,p.z].map(n=>Math.round(n*1e9)).join(":");
const unit=p=>{const n=Math.hypot(p.x,p.y,p.z);return {x:p.x/n,y:p.y/n,z:p.z/n};};
const mix=(a,b,t)=>unit({x:a.x+(b.x-a.x)*t,y:a.y+(b.y-a.y)*t,z:a.z+(b.z-a.z)*t});
const sub=(a,b)=>({x:a.x-b.x,y:a.y-b.y,z:a.z-b.z});
const cross=(a,b)=>({x:a.y*b.z-a.z*b.y,y:a.z*b.x-a.x*b.z,z:a.x*b.y-a.y*b.x});
const dot=(a,b)=>a.x*b.x+a.y*b.y+a.z*b.z;
const kind=b=>b.buildingTypeId??b.kind;
export const groupedField=b=>kind(b)==="garden"&&b.status==="active";
const corners=(cell,size)=>[[-.5,-.5],[.5,-.5],[.5,.5],[-.5,.5]].map(([x,y])=>facePoint(cell.face,cell.x+x,cell.y+y,size));

// World-space union: shared edges cancel even across cube-face seams. Never depends on the camera.
export function buildFieldGroups(settlement,size,allSettlements=[settlement]){
  const fields=settlement.buildings.filter(groupedField).sort((a,b)=>a.id.localeCompare(b.id));
  const cells=new Map(fields.map(b=>[`${b.face}:${b.x}:${b.y}`,b]));
  const protectedCorners=new Set(allSettlements.flatMap(s=>[
    ...s.buildings.filter(b=>s.id!==settlement.id||!groupedField(b)),...(s.usedLands??[]).filter(l=>l.usage>0)
  ]).flatMap(b=>corners(b,size).map(key)));
  const edges=new Map(),neighbors=new Map([...cells.values()].map(b=>[b.id,new Set()]));
  for(const cell of cells.values()){
    const points=corners(cell,size);
    points.forEach((a,i)=>{const b=points[(i+1)%4],edgeKey=[key(a),key(b)].sort().join("|");
      if(!edges.has(edgeKey))edges.set(edgeKey,[]);edges.get(edgeKey).push({a,b,from:key(a),to:key(b),cell});});
  }
  for(const owners of edges.values())if(owners.length===2){neighbors.get(owners[0].cell.id).add(owners[1].cell.id);neighbors.get(owners[1].cell.id).add(owners[0].cell.id);}
  const unseen=new Map([...cells.values()].map(b=>[b.id,b])),groups=[];
  while(unseen.size){
    const first=unseen.values().next().value,queue=[first],members=[];unseen.delete(first.id);
    for(let i=0;i<queue.length;i++){const cell=queue[i];members.push(cell);
      for(const id of neighbors.get(cell.id))if(unseen.has(id)){queue.push(unseen.get(id));unseen.delete(id);}}
    const ids=new Set(members.map(b=>b.id));
    const boundary=[...edges.values()].filter(e=>e.length===1&&ids.has(e[0].cell.id)).map(e=>e[0]);
    const outgoing=new Map();for(const e of boundary){if(!outgoing.has(e.from))outgoing.set(e.from,[]);outgoing.get(e.from).push(e);}
    const remaining=new Set(boundary),rings=[];
    while(remaining.size){
      const start=remaining.values().next().value;let edge=start;const loop=[];
      while(edge&&remaining.has(edge)){
        remaining.delete(edge);loop.push(edge.a);if(edge.to===start.from)break;
        const choices=(outgoing.get(edge.to)??[]).filter(e=>remaining.has(e));
        const incoming=sub(edge.b,edge.a);
        choices.sort((a,b)=>{
          const turn=e=>Math.atan2(dot(cross(incoming,sub(e.b,e.a)),edge.b),dot(incoming,sub(e.b,e.a)));
          return turn(b)-turn(a);
        });edge=choices[0];
      }
      if(loop.length>=3){
        const smooth=[];
        for(let i=0;i<loop.length;i++){
          const prev=loop[(i+loop.length-1)%loop.length],p=loop[i],next=loop[(i+1)%loop.length];
          const convex=dot(cross(sub(p,prev),sub(next,p)),p)>1e-12;
          if(protectedCorners.has(key(p))||!convex){smooth.push(p);continue;}
          const a=mix(p,prev,.22),b=mix(p,next,.22);smooth.push(a);
          for(let t=.25;t<=1;t+=.25)smooth.push(mix(mix(a,p,t),mix(p,b,t),t));
        }
        smooth.push(smooth[0]);rings.push(smooth);
      }
    }
    const center=unit(members.map(b=>facePoint(b.face,b.x,b.y,size)).reduce((sum,p)=>({x:sum.x+p.x,y:sum.y+p.y,z:sum.z+p.z}),{x:0,y:0,z:0}));
    // Pick an actual field cell, not the centroid (which can lie in a hole or on a house).
    const anchor=members.map(cell=>({cell,point:facePoint(cell.face,cell.x,cell.y,size)})).sort((a,b)=>dot(b.point,center)-dot(a.point,center)||a.cell.id.localeCompare(b.cell.id))[0].point;
    groups.push({id:first.id,members:members.map(b=>b.id),rings,anchor});
  }
  return groups;
}

export class FieldGeometryCache {
  constructor(){this.stamp="";this.groups=[];this.builds=0;}
  get(settlements,size){
    const stamp=JSON.stringify([size,settlements.map(s=>[s.id,s.buildings.map(b=>[b.id,b.face,b.x,b.y,kind(b),groupedField(b)]),
      (s.usedLands??[]).map(l=>[l.id,l.face,l.x,l.y,l.usage>0])])]);
    if(stamp!==this.stamp){this.groups=settlements.flatMap(s=>buildFieldGroups(s,size,settlements));this.stamp=stamp;this.builds++;}
    return this.groups;
  }
}

export function drawFieldGroups(ctx,groups,project,{symbols=true,drawSymbol=()=>{},color="#a99f79"}={}){
  for(const group of groups){
    ctx.save();ctx.beginPath();let complete=true;
    for(const ring of group.rings){const points=ring.map(project);if(points.some(p=>!p)){complete=false;continue;}
      points.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();}
    // Clip complete polygons only: no invented closing chord across the far hemisphere.
    if(complete){ctx.fillStyle="rgba(185,169,111,.09)";ctx.fill("evenodd");}
    ctx.strokeStyle=color;ctx.lineWidth=.9;ctx.lineJoin="round";ctx.stroke();
    const anchor=project(group.anchor);if(symbols&&anchor)drawSymbol(ctx,"field",anchor.x,anchor.y,24,.8);
    ctx.restore();
  }
}
