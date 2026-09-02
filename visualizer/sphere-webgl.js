import {waterShoreByte} from './sphere-water.js';
import {parseRenderPacket,RenderPacket} from './sphere-render-packet.js';
const FACE_ORDER=["PositiveX","NegativeX","PositiveY","NegativeY","PositiveZ","NegativeZ"];

const vertexSource=`#version 300 es
precision highp float;
const vec2 positions[3]=vec2[3](vec2(-1.0,-1.0),vec2(3.0,-1.0),vec2(-1.0,3.0));
void main(){gl_Position=vec4(positions[gl_VertexID],0.0,1.0);}`;

const fragmentSource=`#version 300 es
precision highp float;
precision highp sampler2DArray;
uniform vec2 uViewport;
uniform vec2 uCenter;
uniform float uRadius;
uniform mat3 uWorldFromView;
uniform sampler2DArray uTerrain;
uniform sampler2DArray uTerrainPrevious;
uniform float uTerrainMix;
uniform sampler2DArray uWater;
uniform sampler2DArray uWaterPrevious;
uniform float uWaterMix;
uniform sampler2DArray uLakes;
uniform sampler2DArray uElevation;
uniform sampler2DArray uElevationPrevious;
uniform float uElevationMix;
uniform float uContourInterval;
uniform float uContours;
uniform float uSeaLevel;
uniform sampler2D uStars;
uniform float uStarsReady;
uniform float uSeed;
out vec4 outColor;

float hash21(vec2 p){
  p=fract(p*vec2(123.34,345.45));p+=dot(p,p+34.345);return fract(p.x*p.y);
}

vec3 stars(vec2 frag){
  vec2 p=(frag-.5*uViewport)/uViewport.y;
  vec3 sky=vec3(.003,.006,.012);
  for(int layer=0;layer<2;layer++){
    float scale=layer==0?430.0:790.0;
    vec2 cell=floor(p*scale+uSeed*vec2(.013,.021));
    vec2 local=fract(p*scale)-.5;
    float h=hash21(cell+float(layer)*91.7);
    float threshold=layer==0?.9925:.9975;
    float star=smoothstep(.13,0.0,length(local))*smoothstep(threshold,1.0,h);
    vec3 tint=mix(vec3(.48,.65,1.0),vec3(1.0,.78,.60),hash21(cell+17.0));
    sky+=tint*star*(layer==0?.75:1.15);
  }
  return sky;
}

vec3 starBackground(vec2 frag){
  if(uStarsReady<.5)return stars(frag);
  vec2 screen=frag/uViewport;
  float viewportAspect=uViewport.x/uViewport.y;
  vec2 uv=viewportAspect<=2.0
    ?vec2(.5+(screen.x-.5)*viewportAspect/2.0,screen.y)
    :vec2(screen.x,.5+(screen.y-.5)*2.0/viewportAspect);
  return texture(uStars,uv).rgb;
}

vec3 cubeCoordinate(vec3 p){
  vec3 a=abs(p);float layer;vec2 uv;
  if(a.x>=a.y&&a.x>=a.z){
    if(p.x>=0.0){layer=0.0;uv=vec2(-p.z,p.y)/a.x;}
    else{layer=1.0;uv=vec2(p.z,p.y)/a.x;}
  }else if(a.y>=a.z){
    if(p.y>=0.0){layer=2.0;uv=vec2(p.x,-p.z)/a.y;}
    else{layer=3.0;uv=vec2(p.x,p.z)/a.y;}
  }else{
    if(p.z>=0.0){layer=4.0;uv=vec2(p.x,p.y)/a.z;}
    else{layer=5.0;uv=vec2(-p.x,p.y)/a.z;}
  }
  return vec3(uv*.5+.5,layer);
}

vec3 sampleCubeSphere(vec3 p){
  vec3 coordinate=cubeCoordinate(p);
  vec3 land=mix(texture(uTerrainPrevious,coordinate).rgb,texture(uTerrain,coordinate).rgb,uTerrainMix);
  vec2 water=mix(texture(uWaterPrevious,coordinate).rg,texture(uWater,coordinate).rg,uWaterMix);
  vec2 lake=texture(uLakes,coordinate).rg;
  float seaShore=water.r*255.0-128.0;
  float lakeShore=lake.r*255.0-128.0;
  float seaCoverage=smoothstep(-.5,.5,seaShore);
  float lakeCoverage=smoothstep(-.5,.5,lakeShore);
  float shore=max(seaShore,lakeShore);
  float coverage=max(seaCoverage,lakeCoverage);
  float depth=max(water.g,lake.g*lakeCoverage);
  vec3 waterColor=mix(vec3(195.0,220.0,226.0)/255.0,vec3(153.0,194.0,209.0)/255.0,depth);
  vec3 surface=mix(land,waterColor,coverage);
  // Measure the zero crossing in saturated wet/dry coverage, not in the raw
  // signed field. A dry shoreline halo can jump from -1 to -128 without ever
  // becoming water; fwidth(raw shore) treated that discontinuity as a second,
  // square coast. Coverage stays exactly zero there, while a real bank still
  // crosses 0.5 and retains the slope-selected bilinear curve.
  float coastDistance=abs(coverage-.5)/max(fwidth(coverage),.0001);
  float coast=1.0-smoothstep(.32,1.08,coastDistance);
  surface=mix(surface,vec3(57.0,126.0,158.0)/255.0,coast*.94);
  if(uContours>.5){
    float oldElevation=texture(uElevationPrevious,coordinate).r;
    float elevation=mix(oldElevation,texture(uElevation,coordinate).r,uElevationMix);
    float phase=abs(fract(elevation/uContourInterval+.5)-.5)*uContourInterval;
    float contour=1.0-smoothstep(.35,1.05,phase/max(fwidth(elevation),.01));
    float majorPhase=abs(fract(elevation/(uContourInterval*5.0)+.5)-.5)*uContourInterval*5.0;
    float major=1.0-smoothstep(.45,1.2,majorPhase/max(fwidth(elevation),.01));
    vec3 ink=coverage>.5?vec3(116.0,191.0,216.0)/255.0:vec3(184.0,132.0,82.0)/255.0;
    surface=mix(surface,ink,contour*(.24+major*.28));
  }
  return surface;
}

void main(){
  vec2 sphere=(gl_FragCoord.xy-uCenter)/uRadius;
  float q=dot(sphere,sphere);
  vec3 background=starBackground(gl_FragCoord.xy);
  if(q>1.0){
    float halo=exp(-(sqrt(q)-1.0)*54.0)*step(sqrt(q),1.12);
    outColor=vec4(background+vec3(.12,.34,.62)*halo*.36,1.0);return;
  }
  vec3 viewNormal=vec3(sphere.x,sphere.y,sqrt(max(0.0,1.0-q)));
  vec3 worldNormal=normalize(uWorldFromView*viewNormal);
  vec3 base=sampleCubeSphere(worldNormal);
  float diffuse=.86+.14*max(0.0,dot(viewNormal,normalize(vec3(-.34,.48,.81))));
  float limb=pow(1.0-viewNormal.z,3.2);
  vec3 color=base*diffuse+vec3(.08,.28,.56)*limb*.42;
  outColor=vec4(color,1.0);
}`;

function compile(gl,type,source){
  const shader=gl.createShader(type);gl.shaderSource(shader,source);gl.compileShader(shader);
  if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)){const message=gl.getShaderInfoLog(shader);gl.deleteShader(shader);throw new Error(message);}
  return shader;
}

function program(gl){
  const result=gl.createProgram(),vertex=compile(gl,gl.VERTEX_SHADER,vertexSource),fragment=compile(gl,gl.FRAGMENT_SHADER,fragmentSource);
  gl.attachShader(result,vertex);gl.attachShader(result,fragment);gl.linkProgram(result);gl.deleteShader(vertex);gl.deleteShader(fragment);
  if(!gl.getProgramParameter(result,gl.LINK_STATUS)){const message=gl.getProgramInfoLog(result);gl.deleteProgram(result);throw new Error(message);}
  return result;
}

export function previewTexturePixels(face,seaLevel){
  const count=face.resolution?face.resolution*face.resolution:face.elevation.length;
  const pixels=new Uint8Array(count*4);
  for(let index=0;index<count;index++){
    const biome=face.biome[index],forest=Math.max(0,Math.min(1,(face.forest[index]-.3)/.25));
    let r=244-40*forest,g=240-20*forest,b=223-36*forest;
    if(face.elevation[index]<=seaLevel){const amount=Math.max(0,Math.min(1,(seaLevel-face.elevation[index])/240));r=195-42*amount;g=220-26*amount;b=226-17*amount;}
    else if(biome===1){r+=(229-r)*.55;g+=(231-g)*.55;b+=(224-b)*.55;}
    else if(biome===2){r+=(238-r)*.3;g+=(223-g)*.3;b+=(184-b)*.3;}
    else if(biome===5){r+=(200-r)*.45;g+=(220-g)*.45;b+=(211-b)*.45;}
    const offset=index*4;pixels[offset]=r;pixels[offset+1]=g;pixels[offset+2]=b;pixels[offset+3]=255;
  }
  return pixels;
}

export function landTexturePixels(face){
  const count=face.resolution?face.resolution*face.resolution:face.elevation.length;
  const pixels=new Uint8Array(count*4);
  for(let index=0;index<count;index++){
    const biome=face.biome[index],forest=Math.max(0,Math.min(1,(face.forest[index]-.3)/.25));
    let r=244-40*forest,g=240-20*forest,b=223-36*forest;
    if(biome===1){r+=(229-r)*.55;g+=(231-g)*.55;b+=(224-b)*.55;}
    else if(biome===2){r+=(238-r)*.3;g+=(223-g)*.3;b+=(184-b)*.3;}
    else if(biome===5){r+=(200-r)*.45;g+=(220-g)*.45;b+=(211-b)*.45;}
    const offset=index*4;pixels[offset]=r;pixels[offset+1]=g;pixels[offset+2]=b;pixels[offset+3]=255;
  }
  return pixels;
}

export function waterTexturePixels(face,seaLevel){
  const count=face.resolution?face.resolution*face.resolution:face.elevation.length,pixels=new Uint8Array(count*2);
  for(let index=0;index<count;index++){
    const shore=seaLevel-face.elevation[index];pixels[index*2]=waterShoreByte(shore);
    if(shore>=0)pixels[index*2+1]=Math.round(255*Math.max(0,Math.min(1,shore/240)));
  }
  return pixels;
}

export function lakeTexturePixels(size,sample){
  const pixels=new Uint8Array(size*size*2);
  for(let y=0;y<size;y++)for(let x=0;x<size;x++){
    const value=sample(x,y),offset=(y*size+x)*2;
    pixels[offset]=waterShoreByte(value.shore);
    pixels[offset+1]=Math.round(255*Math.max(0,Math.min(1,(value.depth??0)/120)));
  }
  return pixels;
}

function upscaleChannels(source,sourceResolution,targetResolution,channels){
  if(sourceResolution===targetResolution)return source;
  const result=new Uint8Array(targetResolution*targetResolution*channels);
  for(let y=0;y<targetResolution;y++)for(let x=0;x<targetResolution;x++){
    const sourceX=Math.max(0,Math.min(sourceResolution-1,(x+.5)*sourceResolution/targetResolution-.5));
    const sourceY=Math.max(0,Math.min(sourceResolution-1,(y+.5)*sourceResolution/targetResolution-.5));
    const x0=Math.floor(sourceX),y0=Math.floor(sourceY),x1=Math.min(sourceResolution-1,x0+1),y1=Math.min(sourceResolution-1,y0+1);
    const fx=sourceX-x0,fy=sourceY-y0,to=(y*targetResolution+x)*channels;
    for(let channel=0;channel<channels;channel++){
      const top=source[(y0*sourceResolution+x0)*channels+channel]*(1-fx)+source[(y0*sourceResolution+x1)*channels+channel]*fx;
      const bottom=source[(y1*sourceResolution+x0)*channels+channel]*(1-fx)+source[(y1*sourceResolution+x1)*channels+channel]*fx;
      result[to+channel]=Math.round(top*(1-fy)+bottom*fy);
    }
  }
  return result;
}
export function upscaleTexturePixels(source,sourceResolution,targetResolution){return upscaleChannels(source,sourceResolution,targetResolution,4);}

export function upscaleElevation(source,sourceResolution,targetResolution){
  if(sourceResolution===targetResolution)return new Float32Array(source);
  const result=new Float32Array(targetResolution*targetResolution);
  for(let y=0;y<targetResolution;y++)for(let x=0;x<targetResolution;x++){
    const sx=Math.max(0,Math.min(sourceResolution-1,(x+.5)*sourceResolution/targetResolution-.5));
    const sy=Math.max(0,Math.min(sourceResolution-1,(y+.5)*sourceResolution/targetResolution-.5));
    const x0=Math.floor(sx),y0=Math.floor(sy),x1=Math.min(sourceResolution-1,x0+1),y1=Math.min(sourceResolution-1,y0+1),fx=sx-x0,fy=sy-y0;
    const top=source[y0*sourceResolution+x0]*(1-fx)+source[y0*sourceResolution+x1]*fx;
    const bottom=source[y1*sourceResolution+x0]*(1-fx)+source[y1*sourceResolution+x1]*fx;
    result[y*targetResolution+x]=top*(1-fy)+bottom*fy;
  }
  return result;
}

export function chunkTexturePixels(chunk,seaLevel){
  return previewTexturePixels({resolution:chunk.width,elevation:chunk.elevationMeters,forest:chunk.forestCover,biome:chunk.biome},seaLevel);
}
export function chunkLandTexturePixels(chunk){return landTexturePixels({resolution:chunk.width,elevation:chunk.elevationMeters,forest:chunk.forestCover,biome:chunk.biome});}
export function chunkWaterTexturePixels(chunk,seaLevel){return waterTexturePixels({resolution:chunk.width,elevation:chunk.elevationMeters},seaLevel);}

export function worldMatrixForGl(matrix){
  return new Float32Array([matrix[0],matrix[3],matrix[6],matrix[1],matrix[4],matrix[7],matrix[2],matrix[5],matrix[8]]);
}

export function viewMatrixForGl(matrix){return new Float32Array(matrix);}

export class WebGlobeRenderer{
  constructor(canvas){
    this.canvas=canvas;this.available=false;this.frames=0;this.milliseconds=0;
    this.worldMatrix=new Float32Array(9);
    try{
      const gl=canvas.getContext("webgl2",{alpha:false,antialias:false,depth:false,stencil:false,preserveDrawingBuffer:false,powerPreference:"high-performance"});
      if(!gl)return;this.gl=gl;this.program=program(gl);this.vao=gl.createVertexArray();
      this.uniforms=Object.fromEntries(["uViewport","uCenter","uRadius","uWorldFromView","uTerrain","uTerrainPrevious","uTerrainMix","uWater","uWaterPrevious","uWaterMix","uLakes","uElevation","uElevationPrevious","uElevationMix","uContourInterval","uContours","uSeaLevel","uStars","uStarsReady","uSeed"].map(name=>[name,gl.getUniformLocation(this.program,name)]));
      this.available=true;
    }catch(error){this.error=error;}
  }
  initialize({preview,seaLevel,seed,faceSize=preview.resolution}){
    if(!this.available)return false;
    const gl=this.gl,resolution=faceSize;
    const byFace=new Map(preview.faces.map(face=>[face.face,face]));
    const initialFaces=[];
    this.texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RGBA8,resolution,resolution,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++){
      const face=byFace.get(FACE_ORDER[layer]);if(!face)throw new Error(`Нет обзорной текстуры ${FACE_ORDER[layer]}`);
      const overview=landTexturePixels({...face,resolution:preview.resolution});
      const pixels=upscaleChannels(overview,preview.resolution,resolution,4);initialFaces.push(pixels);
      gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RGBA,gl.UNSIGNED_BYTE,pixels);
    }
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    const elevationFilter=gl.getExtension('OES_texture_float_linear')?gl.LINEAR:gl.NEAREST;
    this.elevationTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.elevationTexture);gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.R32F,resolution,resolution,6);
    this.previousElevationTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousElevationTexture);gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.R32F,resolution,resolution,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++){
      const face=byFace.get(FACE_ORDER[layer]),pixels=upscaleElevation(face.elevation,preview.resolution,resolution);
      for(const texture of [this.elevationTexture,this.previousElevationTexture]){gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RED,gl.FLOAT,pixels);}
    }
    for(const texture of [this.elevationTexture,this.previousElevationTexture]){gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,elevationFilter);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,elevationFilter);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);}
    this.previousTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousTexture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RGBA8,resolution,resolution,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++)gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RGBA,gl.UNSIGNED_BYTE,initialFaces[layer]);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    this.lakeTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.lakeTexture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RG8,resolution,resolution,6);
    const emptyLakes=new Uint8Array(resolution*resolution*2);
    for(let layer=0;layer<6;layer++)gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RG,gl.UNSIGNED_BYTE,emptyLakes);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    this.waterTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.waterTexture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RG8,resolution,resolution,6);
    this.previousWaterTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousWaterTexture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RG8,resolution,resolution,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++){
      const face=byFace.get(FACE_ORDER[layer]),water=waterTexturePixels({...face,resolution:preview.resolution},seaLevel);
      const pixels=upscaleChannels(water,preview.resolution,resolution,2);
      for(const texture of [this.waterTexture,this.previousWaterTexture]){gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RG,gl.UNSIGNED_BYTE,pixels);}
    }
    for(const texture of [this.waterTexture,this.previousWaterTexture]){gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);}
    this.seed=seed??0;this.seaLevel=seaLevel;this.textureResolution=resolution;this.chunkUploads=0;this.exactSurfaceReady=false;this.elevationReady=true;this.lakeRevision=0;
    this.terrainTransition=null;this.transitioning=false;this.terrainTransitionDuration=900;
    this.canvas.dataset.snapshotCandidates='0';this.canvas.dataset.snapshots='disabled';return true;
  }
  async updateLakes(sample){
    if(!this.available||!this.lakeTexture)return false;
    const gl=this.gl,size=this.textureResolution,revision=++this.lakeRevision,faces=[];
    this.canvas.dataset.lakes='building';
    for(let layer=0;layer<FACE_ORDER.length;layer++){
      const face=FACE_ORDER[layer];faces.push(lakeTexturePixels(size,(x,y)=>sample(face,x,y)));
      await new Promise(resolve=>setTimeout(resolve,0));
    }
    if(revision!==this.lakeRevision)return false;
    const next=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,next);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RG8,size,size,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++)gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,size,size,1,gl.RG,gl.UNSIGNED_BYTE,faces[layer]);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    const previous=this.lakeTexture;this.lakeTexture=next;gl.deleteTexture(previous);
    this.canvas.dataset.lakes='exact';return true;
  }
  updateChunk(chunk){
    if(!this.available||!this.texture)return false;
    if(this.exactSurfaceReady){this.canvas.dataset.chunkSurfaceUpdates='skipped-after-exact';return false;}
    const layer=FACE_ORDER.indexOf(chunk.face);if(layer<0)return false;
    const gl=this.gl;gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT,1);
    const land=chunkLandTexturePixels(chunk);
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RGBA,gl.UNSIGNED_BYTE,land);
    gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousTexture);
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RGBA,gl.UNSIGNED_BYTE,land);
    gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.waterTexture);
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RG,gl.UNSIGNED_BYTE,chunkWaterTexturePixels(chunk,this.seaLevel));
    gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousWaterTexture);
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RG,gl.UNSIGNED_BYTE,chunkWaterTexturePixels(chunk,this.seaLevel));
    const elevation=new Float32Array(chunk.elevationMeters);
    for(const texture of [this.elevationTexture,this.previousElevationTexture]){gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RED,gl.FLOAT,elevation);}
    this.chunkUploads++;this.canvas.dataset.chunkUploads=String(this.chunkUploads);return true;
  }
  loadStarTexture(url){
    if(!this.available||typeof Image==='undefined')return Promise.resolve(false);
    const gl=this.gl;
    return new Promise(resolve=>{
      const image=new Image();image.decoding='async';
      image.onload=()=>{
        this.starTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,this.starTexture);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,true);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGB8,gl.RGB,gl.UNSIGNED_BYTE,image);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,false);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);gl.generateMipmap(gl.TEXTURE_2D);
        this.starsReady=true;this.canvas.dataset.stars='texture';resolve(true);
      };
      image.onerror=()=>{this.canvas.dataset.stars='procedural';resolve(false);};image.src=url;
    });
  }
  async loadRenderPacket(url){
    if(!this.available||!this.texture)return false;
    const response=await fetch(url,{cache:'no-store'});if(!response.ok)return false;
    if(response.status===204)return false;
    return this.applyRenderPacket(parseRenderPacket(await response.arrayBuffer()),{animatePatches:false});
  }
  finishTerrainTransition(){
    if(!this.terrainTransition)return;
    const gl=this.gl;gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousTexture);
    for(const patch of this.terrainTransition.patches){
      const elevation=patch.resource===RenderPacket.resource.elevation;
      const water=patch.resource===RenderPacket.resource.water;
      gl.bindTexture(gl.TEXTURE_2D_ARRAY,elevation?this.previousElevationTexture:water?this.previousWaterTexture:this.previousTexture);
      gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,patch.x,patch.y,patch.layer,patch.width,patch.height,1,elevation?gl.RED:water?gl.RG:gl.RGBA,elevation?gl.FLOAT:gl.UNSIGNED_BYTE,patch.payload);
    }
    this.terrainTransition=null;this.transitioning=false;this.canvas.dataset.surfaceTransition='idle';
  }
  applyRenderPacket(packet,{animatePatches=true}={}){
    if(!this.available||!this.texture)return false;
    if(animatePatches&&this.terrainTransition)this.finishTerrainTransition();
    const started=performance.now(),gl=this.gl,uploaded=new Set();let patchCommands=0,patchBytes=0;
    const transitionPatches=[];
    for(const command of packet.commands){
      const full=command.opcode===RenderPacket.opcode.texture2DArray,patch=command.opcode===RenderPacket.opcode.texture2DArrayPatch;
      if(!full&&!patch)throw new Error(`Unsupported GPU command: ${command.opcode}`);
      if(command.mipLevel!==0||full&&(command.width!==this.textureResolution||command.height!==this.textureResolution||command.layers!==6)||
        patch&&(command.layers!==1||command.layer>=6||command.x+command.width>this.textureResolution||command.y+command.height>this.textureResolution))
        throw new Error('GPU texture command does not match the active cubed sphere');
      let texture,format,type=gl.UNSIGNED_BYTE,bytesPerPixel;
      if(command.resource===RenderPacket.resource.terrain&&command.format===RenderPacket.format.rgba8){texture=this.texture;format=gl.RGBA;bytesPerPixel=4;}
      else if(command.resource===RenderPacket.resource.water&&command.format===RenderPacket.format.rg8){texture=this.waterTexture;format=gl.RG;bytesPerPixel=2;}
      else if(command.resource===RenderPacket.resource.elevation&&command.format===RenderPacket.format.r32f){texture=this.elevationTexture;format=gl.RED;type=gl.FLOAT;bytesPerPixel=4;}
      else throw new Error(`Unsupported GPU texture resource/format: ${command.resource}/${command.format}`);
      const layerBytes=command.width*command.height*bytesPerPixel;
      if(command.length!==layerBytes*command.layers)throw new Error(`GPU texture payload has an invalid length: ${command.length}`);
      gl.bindTexture(gl.TEXTURE_2D_ARRAY,texture);
      if(patch){
        const payload=type===gl.FLOAT?new Float32Array(command.payload.buffer,command.payload.byteOffset,command.payload.byteLength/4):command.payload;
        gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,command.x,command.y,command.layer,command.width,command.height,1,format,type,payload);
        if(command.resource===RenderPacket.resource.terrain||command.resource===RenderPacket.resource.water||command.resource===RenderPacket.resource.elevation){
          const elevation=command.resource===RenderPacket.resource.elevation,water=command.resource===RenderPacket.resource.water;
          // Cross-fading a sparse water patch exposes its rectangular bounds as
          // a temporary zero crossing. Water already evolves in daily physical
          // steps, so mirror it immediately and animate only terrain/elevation.
          if(animatePatches&&!water)transitionPatches.push({...command,payload:type===gl.FLOAT?new Float32Array(payload):command.payload.slice()});
          else{gl.bindTexture(gl.TEXTURE_2D_ARRAY,elevation?this.previousElevationTexture:water?this.previousWaterTexture:this.previousTexture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,command.x,command.y,command.layer,command.width,command.height,1,elevation?gl.RED:water?gl.RG:gl.RGBA,elevation?gl.FLOAT:gl.UNSIGNED_BYTE,payload);}
        }
        patchCommands++;patchBytes+=command.length;
      }else{
        for(let layer=0;layer<command.layers;layer++){
          const payload=command.payload.subarray(layer*layerBytes,(layer+1)*layerBytes);
          const typed=type===gl.FLOAT?new Float32Array(payload.buffer,payload.byteOffset,payload.byteLength/4):payload;
          gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,command.mipLevel,0,0,layer,command.width,command.height,1,format,type,typed);
          if(command.resource===RenderPacket.resource.terrain){gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousTexture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,command.mipLevel,0,0,layer,command.width,command.height,1,gl.RGBA,gl.UNSIGNED_BYTE,payload);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);}
          if(command.resource===RenderPacket.resource.water){gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousWaterTexture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,command.mipLevel,0,0,layer,command.width,command.height,1,gl.RG,gl.UNSIGNED_BYTE,payload);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.waterTexture);}
          if(command.resource===RenderPacket.resource.elevation){gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousElevationTexture);gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,command.mipLevel,0,0,layer,command.width,command.height,1,gl.RED,gl.FLOAT,typed);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.elevationTexture);}
        }
        uploaded.add(command.resource);
      }
    }
    if(transitionPatches.length){this.terrainTransition={started:performance.now(),patches:transitionPatches};this.transitioning=true;this.canvas.dataset.surfaceTransition='active';}
    if(uploaded.size){if(uploaded.has(RenderPacket.resource.elevation))this.elevationReady=true;this.exactSurfaceReady=uploaded.has(RenderPacket.resource.terrain)&&uploaded.has(RenderPacket.resource.water)&&uploaded.has(RenderPacket.resource.elevation);}
    if(this.exactSurfaceReady)this.canvas.dataset.surface='exact';
    if(patchCommands){this.surfacePatchUploads=(this.surfacePatchUploads??0)+patchCommands;this.surfacePatchBytes=(this.surfacePatchBytes??0)+patchBytes;
      this.canvas.dataset.surfacePatchUploads=String(this.surfacePatchUploads);this.canvas.dataset.surfacePatchBytes=String(this.surfacePatchBytes);this.canvas.dataset.surfacePatchMs=(performance.now()-started).toFixed(2);}
    return this.exactSurfaceReady||patchCommands>0;
  }
  draw({width,height,centerX,centerY,radius,matrix,contours=true,contourInterval=100}){
    if(!this.available||!this.texture)return false;
    const started=performance.now(),gl=this.gl;
    if(this.canvas.width!==width)this.canvas.width=width;if(this.canvas.height!==height)this.canvas.height=height;
    gl.viewport(0,0,width,height);gl.disable(gl.DEPTH_TEST);gl.disable(gl.BLEND);gl.useProgram(this.program);gl.bindVertexArray(this.vao);
    let terrainMix=1;
    if(this.terrainTransition){terrainMix=Math.min(1,(performance.now()-this.terrainTransition.started)/this.terrainTransitionDuration);if(terrainMix>=1)this.finishTerrainTransition();}
    this.transitioning=!!this.terrainTransition;
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);gl.uniform1i(this.uniforms.uTerrain,0);
    gl.activeTexture(gl.TEXTURE1);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.waterTexture);gl.uniform1i(this.uniforms.uWater,1);
    gl.activeTexture(gl.TEXTURE2);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.lakeTexture);gl.uniform1i(this.uniforms.uLakes,2);
    gl.activeTexture(gl.TEXTURE3);gl.bindTexture(gl.TEXTURE_2D,this.starTexture??null);gl.uniform1i(this.uniforms.uStars,3);gl.uniform1f(this.uniforms.uStarsReady,this.starsReady?1:0);
    gl.activeTexture(gl.TEXTURE4);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousTexture);gl.uniform1i(this.uniforms.uTerrainPrevious,4);gl.uniform1f(this.uniforms.uTerrainMix,terrainMix);
    gl.activeTexture(gl.TEXTURE5);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.elevationTexture);gl.uniform1i(this.uniforms.uElevation,5);
    gl.activeTexture(gl.TEXTURE6);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousElevationTexture);gl.uniform1i(this.uniforms.uElevationPrevious,6);gl.uniform1f(this.uniforms.uElevationMix,terrainMix);
    gl.activeTexture(gl.TEXTURE7);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.previousWaterTexture);gl.uniform1i(this.uniforms.uWaterPrevious,7);gl.uniform1f(this.uniforms.uWaterMix,terrainMix);
    gl.uniform1f(this.uniforms.uContours,contours&&this.elevationReady?1:0);gl.uniform1f(this.uniforms.uContourInterval,Math.max(.01,contourInterval));gl.uniform1f(this.uniforms.uSeaLevel,this.seaLevel);
    gl.uniform2f(this.uniforms.uViewport,width,height);gl.uniform2f(this.uniforms.uCenter,centerX,height-centerY);gl.uniform1f(this.uniforms.uRadius,radius);
    const worldMatrix=this.worldMatrix;
    worldMatrix[0]=matrix[0];worldMatrix[1]=matrix[3];worldMatrix[2]=matrix[6];worldMatrix[3]=matrix[1];worldMatrix[4]=matrix[4];worldMatrix[5]=matrix[7];worldMatrix[6]=matrix[2];worldMatrix[7]=matrix[5];worldMatrix[8]=matrix[8];
    gl.uniformMatrix3fv(this.uniforms.uWorldFromView,false,worldMatrix);gl.uniform1f(this.uniforms.uSeed,(this.seed%100000)+1);
    gl.drawArrays(gl.TRIANGLES,0,3);this.frames++;this.milliseconds=performance.now()-started;
    this.canvas.dataset.frames=String(this.frames);this.canvas.dataset.frameMs=this.milliseconds.toFixed(2);return true;
  }
}
