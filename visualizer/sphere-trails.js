import {facePoint} from "./sphere-cartography.js";

const cellKey=c=>`${c.face}:${c.x}:${c.y}`;
export const trailKey=edge=>[cellKey(edge.from),cellKey(edge.to)].sort().join("|");
const unit=p=>{const n=Math.hypot(p.x,p.y,p.z);return {x:p.x/n,y:p.y/n,z:p.z/n};};
const mix=(a,b,t)=>({x:a.x+(b.x-a.x)*t,y:a.y+(b.y-a.y)*t,z:a.z+(b.z-a.z)*t});

// Geometry is world-space and depends on connectivity only, never on traffic,
// zoom, projection or screen collisions. Every edge keeps its own live strength.
export class TrailGeometryCache {
  constructor(){this.key=null;this.value=[];this.builds=0;}
  get(trails,size){
    const edges=new Map(trails.map(edge=>[trailKey(edge),edge]));
    const key=`${size}:`+[...edges.keys()].sort().join(";");
    if(key===this.key)return this.value;
    this.key=key;this.builds++;
    const vertices=new Map(),adjacency=new Map(),used=new Set(),result=[];
    for(const [id,edge] of [...edges].sort(([a],[b])=>a.localeCompare(b))){
      for(const cell of [edge.from,edge.to]){
        const k=cellKey(cell);vertices.set(k,facePoint(cell.face,cell.x,cell.y,size));
        if(!adjacency.has(k))adjacency.set(k,[]);adjacency.get(k).push(id);
      }
    }
    function walk(start,first){
      const nodes=[start],ids=[];let node=start,id=first;
      while(id&&!used.has(id)){
        used.add(id);ids.push(id);
        const edge=edges.get(id),a=cellKey(edge.from),b=cellKey(edge.to);
        node=node===a?b:a;nodes.push(node);
        if(adjacency.get(node).length!==2)break;
        id=adjacency.get(node).find(k=>!used.has(k));
      }
      const closed=nodes.length>2&&nodes[0]===nodes.at(-1),count=closed?nodes.length-1:nodes.length;
      const corners=Array.from({length:count},(_,i)=>{
        const b=vertices.get(nodes[i]);
        if(!closed&&(i===0||i===count-1))return [b];
        const entry=mix(b,vertices.get(nodes[(i+count-1)%count]),.22);
        const exit=mix(b,vertices.get(nodes[(i+1)%count]),.22);
        return Array.from({length:7},(_,j)=>{const t=j/6;return unit(mix(mix(entry,b,t),mix(b,exit,t),t));});
      });
      for(let i=0;i<ids.length;i++){
        const a=corners[i],b=corners[(i+1)%count];
        result.push({key:ids[i],points:[...a.slice(Math.floor(a.length/2)),...b.slice(0,Math.floor(b.length/2)+1)]});
      }
    }
    for(const node of [...adjacency.keys()].sort())if(adjacency.get(node).length!==2)
      for(const id of adjacency.get(node))if(!used.has(id))walk(node,id);
    // Remaining components are closed loops; do not introduce a seam at their start.
    for(const id of [...edges.keys()].sort())if(!used.has(id))walk(cellKey(edges.get(id).from),id);
    this.value=result;return result;
  }
}
