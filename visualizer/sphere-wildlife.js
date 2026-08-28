import {facePoint} from "./sphere-cartography.js";

// A schematic spherical range, in world coordinates. Camera motion cannot
// reseed, shift or resize the habitat; this is separate from terrain tile caches.
export function wildlifeZonePoints(group,faceSize,segments=32){
  const p=facePoint(group.face,group.x,group.y,faceSize),n=[p.x,p.y,p.z];
  const tangent=Math.abs(n[1])<.9?[n[2],0,-n[0]]:[0,-n[2],n[1]];
  const length=Math.hypot(...tangent),u=tangent.map(v=>v/length);
  const v=[n[1]*u[2]-n[2]*u[1],n[2]*u[0]-n[0]*u[2],n[0]*u[1]-n[1]*u[0]];
  const radius=2*group.radiusCells/faceSize;
  return Array.from({length:segments+1},(_,i)=>{
    const a=2*Math.PI*i/segments;
    const xyz=n.map((value,j)=>value*Math.cos(radius)+(u[j]*Math.cos(a)+v[j]*Math.sin(a))*Math.sin(radius));
    return {x:xyz[0],y:xyz[1],z:xyz[2]};
  });
}
export function wildlifeSummary(groups){
  return `Дичь · наблюдатель: ${groups.length} подвижных групп, ${groups.filter(g=>g.alert>=.05).length} встревожены. `+
    "Пунктир — схематичная область обитания, не граница поселения. Группы могут уйти за знакомую людям территорию.";
}
