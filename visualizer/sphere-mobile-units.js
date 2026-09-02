function sameCell(a,b){return a&&b&&a.face===b.face&&a.x===b.x&&a.y===b.y;}
function expeditionKey(scout){return `${scout.id}:${scout.departureDay??"unknown"}`;}
function movementKey(scout){return `${expeditionKey(scout)}:${scout.phase}:${scout.routeIndex??0}:${scout.path?.length??0}:${scout.face}:${scout.x}:${scout.y}`;}

export function scoutMovementPath(previous,next){
  const route=next.path??[];
  const current={face:next.face,x:next.x,y:next.y};
  if(!route.length)return [current];
  if(!previous||previous.id!==next.id){
    const cells=[...route];
    if(next.phase==="returning"||next.phase==="returned"){
      const to=next.phase==="returned"?0:Math.max(0,Math.min(route.length-1,next.routeIndex??0));
      for(let i=route.length-2;i>=to;i--)cells.push(route[i]);
    }
    if(!sameCell(cells.at(-1),current))cells.push(current);
    return cells;
  }
  const from=Math.max(0,Math.min(route.length-1,previous.routeIndex??0));
  const to=Math.max(0,Math.min(route.length-1,next.routeIndex??0));
  let cells=[];
  if(previous.phase==="outbound"&&next.phase!=="outbound"){
    for(let i=from;i<route.length;i++)cells.push(route[i]);
    for(let i=route.length-2;i>=to;i--)cells.push(route[i]);
  }else if(to>=from){for(let i=from;i<=to;i++)cells.push(route[i]);}
  else{for(let i=from;i>=to;i--)cells.push(route[i]);}
  if(!sameCell(cells.at(-1),current))cells.push(current);
  return cells.length?cells:[current];
}

export function interpolateUnitPath(points,progress){
  if(!points.length)return null;if(points.length===1)return points[0];
  const scaled=Math.max(0,Math.min(.999999,progress))*(points.length-1),index=Math.floor(scaled),t=scaled-index;
  const a=points[index],b=points[Math.min(points.length-1,index+1)];
  const x=a.x+(b.x-a.x)*t,y=a.y+(b.y-a.y)*t,z=a.z+(b.z-a.z)*t,length=Math.hypot(x,y,z)||1;
  return{x:x/length,y:y/length,z:z/length};
}

export class MobileUnitLayer{
  constructor(canvas,{pointForCell,project,drawWildlife=()=>{},wildlifeVisible=()=>true,now=()=>performance.now(),request=fn=>requestAnimationFrame(fn),cancel=id=>cancelAnimationFrame(id),duration=4200,fps=30}={}){
    Object.assign(this,{canvas,pointForCell,project,drawWildlife,wildlifeVisible,now,request,cancel,duration,fps});this.context=canvas.getContext("2d");this.units=new Map();this.wildlife=new Map();this.completed=new Map();this.frame=0;this.pixelRatio=1;this.worldDay=0;this.lastPaint=0;
  }
  resize(width,height,pixelRatio=1){this.pixelRatio=pixelRatio;this.canvas.width=Math.max(1,Math.round(width*pixelRatio));this.canvas.height=Math.max(1,Math.round(height*pixelRatio));this.canvas.style.width=`${width}px`;this.canvas.style.height=`${height}px`;this.render();}
  update(scouts,wildlife,worldDay,{animate=true}={}){
    this.worldDay=worldDay;const incoming=new Set(),started=this.now();
    for(const scout of scouts??[]){
      const key=expeditionKey(scout),marker=movementKey(scout);incoming.add(scout.id);
      const existing=this.units.get(scout.id);
      if(this.completed.has(key)&&scout.phase==="returned"){
        if(existing?.key!==key)this.units.delete(scout.id);
        continue;
      }
      if(existing?.key===key&&existing.marker===marker){existing.scout=scout;continue;}
      const previous=existing?.key===key?existing.scout:null,cells=scoutMovementPath(previous,scout),points=cells.map(this.pointForCell);
      const from=existing?.key===key?existing.point:points[0],path=from?[from,...points.filter((p,i)=>i||Math.hypot(p.x-from.x,p.y-from.y,p.z-from.z)>.000001)]:points;
      this.units.set(scout.id,{scout,key,marker,path,point:path.at(-1),started,duration:animate&&path.length>1?this.duration:0});
    }
    for(const [id,unit] of this.units)if(!incoming.has(id)&&unit.scout.phase!=="lost")this.units.delete(id);
    const wildlifeIds=new Set();
    for(const group of wildlife??[]){
      wildlifeIds.add(group.id);const existing=this.wildlife.get(group.id),target=this.pointForCell(group);
      const changed=!existing||!sameCell(existing.group,group),path=changed&&existing?.point?[existing.point,target]:[target];
      this.wildlife.set(group.id,{group,path,point:changed?existing?.point??target:existing?.point??target,started,duration:animate&&changed?this.duration:0});
    }
    for(const id of this.wildlife.keys())if(!wildlifeIds.has(id))this.wildlife.delete(id);
    for(const [key,day] of this.completed)if(worldDay-day>400)this.completed.delete(key);
    this.render();this.start();
  }
  start(){if(this.frame)return;const tick=timestamp=>{this.frame=0;const now=Number.isFinite(timestamp)?timestamp:this.now(),interval=1000/this.fps;
    let active=true;if(now-this.lastPaint>=interval){this.lastPaint=now;active=this.render();}
    if(active)this.frame=this.request(tick);};this.frame=this.request(tick);}
  render(){
    const ctx=this.context,ratio=this.pixelRatio;ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,this.canvas.width/ratio,this.canvas.height/ratio);let active=false;
    for(const [id,unit] of this.units){
      const elapsed=this.now()-unit.started,progress=unit.duration?Math.min(1,elapsed/unit.duration):1;
      unit.point=interpolateUnitPath(unit.path,progress)??unit.point;if(progress<1)active=true;
      if(progress>=1&&unit.scout.phase==="returned"){this.completed.set(unit.key,this.worldDay);this.units.delete(id);continue;}
      if(unit.scout.phase==="lost"&&this.worldDay-(unit.scout.lostDay??this.worldDay)>14){this.units.delete(id);continue;}
      const screen=this.project(unit.point);if(!screen||screen.z<=.012)continue;this.drawPennant(ctx,screen.x,screen.y,unit.scout);
    }
    if(this.wildlifeVisible())for(const unit of this.wildlife.values()){
      const elapsed=this.now()-unit.started,progress=unit.duration?Math.min(1,elapsed/unit.duration):1;
      unit.point=interpolateUnitPath(unit.path,progress)??unit.point;if(progress<1)active=true;
      const screen=this.project(unit.point);if(!screen||screen.z<=.012)continue;this.drawWildlife(ctx,screen.x,screen.y,unit.group);
    }
    return active;
  }
  drawPennant(ctx,x,y,scout){
    // The unit follows the continuous camera projection. Pixel snapping made
    // every tiny camera change appear as a one-pixel jump.
    const lost=scout.phase==="lost";ctx.save();ctx.translate(x,y);
    ctx.shadowColor="rgba(15,25,20,.24)";ctx.shadowBlur=3;ctx.shadowOffsetY=1;ctx.lineCap="round";ctx.lineJoin="round";
    if(lost){ctx.strokeStyle="#6f5149";ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(-6,-6);ctx.lineTo(6,6);ctx.moveTo(6,-6);ctx.lineTo(-6,6);ctx.stroke();ctx.restore();return;}
    ctx.strokeStyle="#3d4a42";ctx.fillStyle=scout.phase==="returning"?"#d19a45":"#b94e3f";ctx.lineWidth=1.4;
    ctx.beginPath();ctx.moveTo(-5,9);ctx.lineTo(-5,-10);ctx.stroke();ctx.beginPath();ctx.moveTo(-4,-9);ctx.lineTo(9,-5);ctx.lineTo(-4,0);ctx.closePath();ctx.fill();ctx.stroke();
    ctx.strokeStyle="#f3e8c8";ctx.lineWidth=1.1;ctx.beginPath();ctx.arc(1,-5,4,-1.2,1.2);ctx.stroke();ctx.beginPath();ctx.moveTo(2,-9);ctx.lineTo(2,-1);ctx.stroke();
    if(scout.travelMode==="raft"){ctx.strokeStyle="#43809a";ctx.beginPath();ctx.moveTo(-9,11);ctx.quadraticCurveTo(0,15,9,11);ctx.moveTo(-7,9);ctx.lineTo(7,9);ctx.stroke();}
    ctx.restore();
  }
  dispose(){if(this.frame)this.cancel(this.frame);this.frame=0;this.units.clear();this.wildlife.clear();this.completed.clear();}
}
