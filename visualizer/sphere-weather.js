import { FACE_NAMES, facePoint, locateFace } from './sphere-cartography.js';

const clamp = (v, lo=0, hi=1) => Math.max(lo, Math.min(hi,v));
const fields = {temperature:10,rain:10,snow:10,ice:1000,leafOff:1,windX:10000,windY:10000,windZ:10000};
const fieldEntries=Object.entries(fields);
export function createWeatherSampler(data, faceSize) {
  if (!data) return null;
  const n=data.resolution, faces=new Map(FACE_NAMES.map((f,i)=>[f,i]));
  const locals=new Map((data.local?.indices??[]).map((id,i)=>[id,i])), patches=new Map();
  function index(face,x,y) {
    if(x<0||y<0||x>=n||y>=n) {
      const p=locateFace(facePoint(face,x,y,n));face=p.face;
      x=clamp(Math.floor((p.u+1)*n/2),0,n-1);y=clamp(Math.floor((p.v+1)*n/2),0,n-1);
    }
    return (faces.get(face)*n+y)*n+x;
  }
  return location => {
    const gx=(location.u+1)*n/2-.5,gy=(location.v+1)*n/2-.5,x=Math.floor(gx),y=Math.floor(gy);
    const key=(faces.get(location.face)*(n+2)+y+1)*(n+2)+x+1;
    let patch=patches.get(key);
    if(!patch){
      const indices=[[0,0],[1,0],[1,1],[0,1]].map(([dx,dy])=>index(location.face,x+dx,y+dy));patch={};
      for(const [field,scale] of fieldEntries){const v=indices.map(i=>data[field][i]/scale);patch[field]=[v[0],v[1]-v[0],v[3]-v[0],v[2]-v[3]-v[1]+v[0]];}
      patches.set(key,patch);
    }
    const result={exact:false,walking:null};
    for(const [field] of fieldEntries){const c=patch[field];result[field]=c[0]+c[1]*(gx-x)+c[2]*(gy-y)+c[3]*(gx-x)*(gy-y);}
    const cx=clamp(Math.floor((location.u+1)*faceSize/2),0,faceSize-1),cy=clamp(Math.floor((location.v+1)*faceSize/2),0,faceSize-1);
    const row=locals.get((faces.get(location.face)*faceSize+cy)*faceSize+cx);
    if(row!==undefined){
      result.exact=true;
      for(const field of ['temperature','snow','ice'])result[field]=data.local[field][row]/fields[field];
      result.walking=data.local.walking[row]<0 ? -1 : data.local.walking[row]/100;
    }
    return result;
  };
}

const CLIMATE_MISSING=-2147483648;
export function createClimateSampler(data) {
  const climate=data?.climate;
  if(!climate?.resolution||climate.months!==12)return null;
  const n=climate.resolution,cellCount=6*n*n,faces=new Map(FACE_NAMES.map((face,index)=>[face,index]));
  const scales={temperature:10,rain:10,wind:100,windX:10000,windY:10000,windZ:10000,
    latestTemperature:10,latestRain:10,latestWind:100,latestWindX:10000,latestWindY:10000,latestWindZ:10000};
  return location=>{
    const face=faces.get(location.face);if(face===undefined)return [];
    const x=clamp(Math.floor((location.u+1)*n/2),0,n-1),y=clamp(Math.floor((location.v+1)*n/2),0,n-1);
    const cell=(face*n+y)*n+x;
    return Array.from({length:12},(_,month)=>{
      const index=month*cellCount+cell,result={month,sampleDays:climate.sampleDays[month]??0,latestSampleDays:climate.latestSampleDays?.[month]??0};
      for(const [field,scale] of Object.entries(scales)){
        const value=climate[field]?.[index];result[field]=value===undefined||value===CLIMATE_MISSING?null:value/scale;
      }
      return result;
    });
  };
}

export function climateSeries(sample,locations) {
  if(!sample||!locations.length)return [];
  const fields=['temperature','rain','wind','windX','windY','windZ','latestTemperature','latestRain','latestWind','latestWindX','latestWindY','latestWindZ'];
  const series=Array.from({length:12},(_,month)=>({month,sampleDays:0,latestSampleDays:0,count:0,fieldCounts:{},...Object.fromEntries(fields.map(field=>[field,0]))}));
  for(const location of locations)for(const item of sample(location)){
    if(item.temperature===null)continue;
    const target=series[item.month];target.sampleDays=Math.max(target.sampleDays,item.sampleDays);target.latestSampleDays=Math.max(target.latestSampleDays,item.latestSampleDays);target.count++;
    for(const field of fields)if(item[field]!==null){target[field]+=item[field];target.fieldCounts[field]=(target.fieldCounts[field]??0)+1;}
  }
  return series.map(item=>item.count?{month:item.month,sampleDays:item.sampleDays,latestSampleDays:item.latestSampleDays,
    ...Object.fromEntries(fields.map(field=>[field,item.fieldCounts[field]?item[field]/item.fieldCounts[field]:null]))}:
    {month:item.month,sampleDays:0,latestSampleDays:0,...Object.fromEntries(fields.map(field=>[field,null]))});
}

export function winterSymbol(kind, weather) {
  if(!weather)return kind;
  if(['deciduous','fruit_tree','nut_tree'].includes(kind)&&weather.leafOff>.5)return 'bare_tree';
  if(kind==='conifer'&&weather.snow>4)return 'snow_conifer';
  if(kind==='grass'&&weather.snow>8)return 'snow';
  return kind;
}

export function weatherText(w,{water=false}={}) {
  if(!w)return 'Погодные данные недоступны для этого сценария.';
  return `${w.temperature.toFixed(1)} °C · осадки ${w.rain.toFixed(1)} мм/сут · снег ${w.snow.toFixed(0)} мм вод. экв.`+
    (water&&w.ice>.005?` · лёд ${Math.round(w.ice*100)} см${w.exact?'':' (климатическая оценка)'}`:'')+
    (w.walking===-1?' · водный переход закрыт':w.walking!==null?` · погодная стоимость пути ×${w.walking.toFixed(2)}`:'')+
    (w.exact?' · местный расчёт снега и льда':' · обзорная погода; проходимость не подтверждена');
}

// The weather tint is independently cached. It never edits the terrain raster,
// hydrology, geometry versions, symbol anchors or chunk residency.
export class WeatherTint {
  constructor(createCanvas) {this.canvas=createCanvas();this.builds=0;this.milliseconds=0;}
  draw(context,{key,width,height,rayAt,sample,mode,winter,landAt}) {
    if(!sample||(mode==='none'&&!winter))return;
    if(this.key!==key){
      const start=performance.now();this.key=key;this.builds++;
      // At most ~36K samples even on a Retina/4K display.
      const scale=Math.min(1,240/width,150/height),w=Math.max(1,Math.round(width*scale)),h=Math.max(1,Math.round(height*scale));
      this.canvas.width=w;this.canvas.height=h;
      const ctx=this.canvas.getContext('2d'),image=ctx.createImageData(w,h);
      for(let y=0;y<h;y++)for(let x=0;x<w;x++){
        const px=(x+.5)*width/w,py=(y+.5)*height/h,point=rayAt(px,py);if(!point)continue;
        const weather=sample(locateFace(point)),i=(y*w+x)*4;
        let r=0,g=0,b=0,a=0;
        const coat=(R,G,B,A)=>{const next=A+a*(1-A);if(next>0){r=(R*A+r*a*(1-A))/next;g=(G*A+g*a*(1-A))/next;b=(B*A+b*a*(1-A))/next;}a=next;};
        if(winter){
          const land=landAt?.(px,py)??1;
          coat(246,251,251,clamp(weather.snow/30)*.6*land);
          coat(224,241,246,clamp(weather.ice/.15)*.65*(1-land));
        }
        if(mode==='temperature'){const t=clamp((weather.temperature+15)/45);coat(65+181*t,137-34*t,210-144*t,.3);}
        if(mode==='rain')coat(78,119,186,clamp(weather.rain/12)*.42);
        if(mode==='wind')coat(66,145,148,clamp(Math.hypot(weather.windX,weather.windY,weather.windZ)*3)*.26);
        image.data[i]=r;image.data[i+1]=g;image.data[i+2]=b;image.data[i+3]=a*255;
      }
      ctx.putImageData(image,0,0);this.milliseconds=performance.now()-start;
    }
    context.drawImage(this.canvas,0,0,width,height);
  }
}

export const WEATHER_EFFECT_LIMIT=96;
export const WEATHER_EFFECT_FPS=12;
export function weatherEffectSites({width,height,rayAt,sample,toView}) {
  if(!sample)return [];
  const sites=[],columns=12,rows=8;
  for(let y=0;y<rows;y++)for(let x=0;x<columns;x++){
    const id=y*columns+x,px=(x+.2+((id*37)%61)/100)*width/columns,py=(y+.2+((id*19)%61)/100)*height/rows;
    const point=rayAt(px,py);if(!point)continue;
    const w=sample(locateFace(point)),v=toView({x:w.windX,y:w.windY,z:w.windZ}),length=Math.hypot(v.x,v.y);
    sites.push({x:px,y:py,id,rain:w.rain,snow:w.temperature<=0,wind:length,dx:length>1e-6?v.x/length:1,dy:length>1e-6?-v.y/length:0});
  }
  return sites;
}

// Separate transparent canvas; only these bounded strokes animate. setTimeout
// avoids 60/120 Hz wakeups when the target is 12 fps. No requests to the server.
export class WeatherEffects {
  constructor(canvas,{hidden=()=>document.hidden,reduced=()=>matchMedia('(prefers-reduced-motion: reduce)').matches}={}) {
    this.canvas=canvas;this.context=canvas.getContext('2d');this.hidden=hidden;this.reduced=reduced;this.timer=null;this.frames=0;
  }
  update({key,width,height,pixelRatio,sites,enabled,arrows,geometry}) {
    this.enabled=enabled;this.arrows=arrows;this.geometry=geometry;
    const active=enabled||arrows;
    if(this.key!==key||this.active!==active){this.key=key;this.active=active;this.sites=active?sites():[];}
    if(this.canvas.width!==Math.round(width*pixelRatio)||this.canvas.height!==Math.round(height*pixelRatio)){
      this.canvas.width=Math.round(width*pixelRatio);this.canvas.height=Math.round(height*pixelRatio);
    }
    this.width=width;this.height=height;this.pixelRatio=pixelRatio;
    this.refresh();
  }
  refresh() {
    this.stop();this.draw(this.reduced()?0:performance.now());
    if(this.enabled&&!this.hidden()&&!this.reduced()&&this.sites?.length)this.queue();
  }
  stop(){if(this.timer!==null)clearTimeout(this.timer);this.timer=null;}
  queue(){this.timer=setTimeout(()=>{this.timer=null;if(this.hidden()||!this.enabled)return;this.draw(performance.now());if(!this.reduced())this.queue();},1000/WEATHER_EFFECT_FPS);}
  draw(time){
    if(!this.width)return;
    const ctx=this.context;ctx.setTransform(this.pixelRatio,0,0,this.pixelRatio,0,0);ctx.clearRect(0,0,this.width,this.height);
    if(this.hidden()||(!this.enabled&&!this.arrows))return;
    this.frames++;this.canvas.dataset.frames=String(this.frames);this.canvas.dataset.sites=String(this.sites?.length??0);
    ctx.save();ctx.beginPath();ctx.arc(this.geometry.centerX,this.geometry.centerY,this.geometry.radius,0,Math.PI*2);ctx.clip();
    ctx.lineWidth=1.2;ctx.lineCap='round';
    for(const s of this.sites??[]){
      if(this.arrows&&s.id%3===0&&s.wind>.01){
        const length=10+Math.min(18,s.wind*100),x=s.x+s.dx*length,y=s.y+s.dy*length;
        ctx.strokeStyle='rgba(36,102,118,.65)';ctx.beginPath();ctx.moveTo(s.x,s.y);ctx.lineTo(x,y);
        ctx.moveTo(x-s.dx*5+s.dy*3,y-s.dy*5-s.dx*3);ctx.lineTo(x,y);ctx.lineTo(x-s.dx*5-s.dy*3,y-s.dy*5+s.dx*3);ctx.stroke();
      }
      if(!this.enabled)continue;
      const phase=(time/2200+s.id*.618)%1,alpha=Math.sin(phase*Math.PI);
      if(s.rain>.3){
        const x=s.x+(phase-.5)*s.dx*18,y=s.y+(phase-.5)*(s.snow?24:46);
        ctx.strokeStyle=s.snow?`rgba(103,153,180,${alpha*.7})`:`rgba(42,110,157,${alpha*Math.min(.7,.2+s.rain/16)})`;
        ctx.beginPath();ctx.moveTo(x,y);ctx.lineTo(x+(s.snow?2:s.dx*3),y+(s.snow?3:8));ctx.stroke();
      }else if(s.wind>.025&&s.id%2===0){
        const shift=(phase-.5)*42,x=s.x+s.dx*shift,y=s.y+s.dy*shift;
        ctx.strokeStyle=`rgba(77,136,149,${alpha*.42})`;ctx.beginPath();ctx.moveTo(x,y);ctx.quadraticCurveTo(x+s.dx*12-s.dy*2,y+s.dy*12+s.dx*2,x+s.dx*25,y+s.dy*25);ctx.stroke();
      }
    }
    ctx.restore();
  }
}
