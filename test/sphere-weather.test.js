import {test} from 'node:test';
import assert from 'node:assert/strict';
import {FACE_NAMES,facePoint,locateFace} from '../visualizer/sphere-cartography.js';
import {createWeatherSampler,winterSymbol,weatherText,WeatherTint,WeatherEffects,weatherEffectSites,WEATHER_EFFECT_LIMIT} from '../visualizer/sphere-weather.js';

function data(n=4){
  const values={resolution:n,revision:0,local:{indices:[],temperature:[],snow:[],ice:[],walking:[]}};
  for(const [field,v] of Object.entries({temperature:100,rain:40,snow:20,ice:250,leafOff:1,windX:0,windY:0,windZ:800}))values[field]=Array(6*n*n).fill(v);
  return values;
}
test('weather units and local overrides do not mistake overview ice for a confirmed crossing',()=>{
  const d=data(),p=locateFace(facePoint('PositiveX',1,1,8));
  d.local={indices:[9],temperature:[-20],snow:[120],ice:[190],walking:[140]};
  const sample=createWeatherSampler(d,8),w=sample(p);
  assert.equal(w.temperature,-2);assert.equal(w.snow,12);assert.equal(w.ice,.19);assert.equal(w.walking,1.4);assert.equal(w.exact,true);
  const overview=sample(locateFace(facePoint('NegativeX',1,1,8)));
  assert.equal(overview.rain,4);assert.equal(overview.walking,null);assert.equal(overview.exact,false);
  assert.match(weatherText(overview),/не подтверждена/);
  assert.doesNotMatch(weatherText(overview),/лёд/);
  assert.match(weatherText(overview,{water:true}),/климатическая оценка/);
  d.local.walking[0]=-1;assert.equal(sample(p).walking,-1);
});
test('cube face borders and poles sample finite continuous weather',()=>{
  const d=data(16);
  for(let f=0;f<6;f++)for(let y=0;y<16;y++)for(let x=0;x<16;x++)d.temperature[(f*16+y)*16+x]=facePoint(FACE_NAMES[f],x,y,16).y*100;
  const sample=createWeatherSampler(d,416);
  const a=sample(locateFace({x:1-1e-8,y:.3,z:1})),b=sample(locateFace({x:1+1e-8,y:.3,z:1}));
  assert.ok(Math.abs(a.temperature-b.temperature)<.02);
  for(const f of FACE_NAMES)for(const x of [-.5,7.5,15.5])for(const y of [-.5,7.5,15.5]){
    const w=sample(locateFace(facePoint(f,x,y,16)));assert.ok(Number.isFinite(w.temperature));assert.ok(Number.isFinite(w.windZ));
  }
});
test('winter switches only the glyph and keeps evergreens distinct',()=>{
  assert.equal(winterSymbol('deciduous',{leafOff:1,snow:0}),'bare_tree');
  assert.equal(winterSymbol('deciduous',{leafOff:0,snow:0}),'deciduous');
  assert.equal(winterSymbol('conifer',{leafOff:1,snow:9}),'snow_conifer');
  assert.equal(winterSymbol('house',{leafOff:1,snow:9}),'house');
});
function fakeCanvas(){const ctx=new Proxy({createImageData:(w,h)=>({data:new Uint8ClampedArray(w*h*4)})},{get:(o,k)=>o[k]??(()=>{})});return {width:0,height:0,dataset:{},getContext:()=>ctx};}
test('tint cache is bounded, reused and costs nothing when all tint is off',()=>{
  const target=fakeCanvas(),tint=new WeatherTint(fakeCanvas);let calls=0;
  const args={key:'day1-camera1',width:4000,height:2000,rayAt:()=>({x:1,y:0,z:0}),sample:()=>{calls++;return {snow:10,ice:0,temperature:3};},mode:'none',winter:true,landAt:()=>1};
  tint.draw(target.getContext(),args);assert.ok(calls<=36000);const first=calls;
  tint.draw(target.getContext(),args);assert.equal(calls,first);assert.equal(tint.builds,1);
  tint.draw(target.getContext(),{...args,key:'day2',winter:false});assert.equal(calls,first);
  tint.draw(target.getContext(),{...args,key:'day2'});assert.equal(tint.builds,2);
});
test('weather effects bounded independently of viewport size and empty outside globe',()=>{
  const args={width:8000,height:4000,rayAt:()=>({x:1,y:0,z:0}),sample:createWeatherSampler(data(),416),toView:p=>p};
  const sites=weatherEffectSites(args);assert.equal(sites.length,WEATHER_EFFECT_LIMIT);assert.ok(sites.every(s=>s.rain===4));
  assert.deepEqual(weatherEffectSites({...args,rayAt:()=>null}),[]);
});
test('off / hidden / reduced motion stop timers; static wind remains optional',()=>{
  const canvas=fakeCanvas();let hidden=false,reduced=false;
  const effects=new WeatherEffects(canvas,{hidden:()=>hidden,reduced:()=>reduced});
  const args={key:'camera',width:800,height:500,pixelRatio:1,sites:()=>[{id:0,x:20,y:20,rain:4,snow:false,wind:.08,dx:1,dy:0}],enabled:true,arrows:false,geometry:{centerX:400,centerY:250,radius:200}};
  effects.update(args);assert.notEqual(effects.timer,null);
  effects.update({...args,enabled:false});assert.equal(effects.timer,null);
  reduced=true;effects.update(args);assert.equal(effects.timer,null);assert.ok(effects.frames>0);
  hidden=true;effects.refresh();assert.equal(effects.timer,null);
  hidden=false;reduced=false;effects.refresh();assert.notEqual(effects.timer,null);effects.stop();
});
