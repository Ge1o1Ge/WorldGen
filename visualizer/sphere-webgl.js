const FACE_ORDER=["PositiveX","NegativeX","PositiveY","NegativeY","PositiveZ","NegativeZ"];
const SNAPSHOT_POOL_SIZE=4;

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
uniform sampler2D uStars;
uniform float uStarsReady;
uniform sampler2D uSnapshot0;
uniform sampler2D uSnapshot1;
uniform sampler2D uSnapshot2;
uniform sampler2D uSnapshot3;
uniform int uSnapshotCount;
uniform mat3 uSnapshotViewFromWorld[4];
uniform vec2 uSnapshotViewport[4];
uniform vec2 uSnapshotCenter[4];
uniform float uSnapshotRadius[4];
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

vec3 sampleCubeSphere(vec3 p){
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
  return texture(uTerrain,vec3(uv*.5+.5,layer)).rgb;
}

vec4 sampleSnapshot(sampler2D source,int index,vec3 worldNormal){
  vec3 snapshotView=uSnapshotViewFromWorld[index]*worldNormal;
  if(snapshotView.z<=.025)return vec4(0.0);
  vec2 pixel=uSnapshotCenter[index]+snapshotView.xy*uSnapshotRadius[index];
  vec2 uv=pixel/uSnapshotViewport[index];
  if(any(lessThan(uv,vec2(0.0)))||any(greaterThan(uv,vec2(1.0))))return vec4(0.0);
  vec4 value=texture(source,uv);
  value.a*=smoothstep(.025,.11,snapshotView.z);
  return value;
}

vec4 behind(vec4 front,vec4 back){
  float alpha=front.a+back.a*(1.0-front.a);
  vec3 premultiplied=front.rgb*front.a+back.rgb*back.a*(1.0-front.a);
  return vec4(alpha>0.0?premultiplied/alpha:vec3(0.0),alpha);
}

vec4 sampleSnapshotPool(vec3 worldNormal){
  vec4 result=vec4(0.0);
  if(uSnapshotCount>0)result=behind(result,sampleSnapshot(uSnapshot0,0,worldNormal));
  if(uSnapshotCount>1)result=behind(result,sampleSnapshot(uSnapshot1,1,worldNormal));
  if(uSnapshotCount>2)result=behind(result,sampleSnapshot(uSnapshot2,2,worldNormal));
  if(uSnapshotCount>3)result=behind(result,sampleSnapshot(uSnapshot3,3,worldNormal));
  return result;
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
  vec4 snapshot=sampleSnapshotPool(worldNormal);
  color=mix(color,snapshot.rgb,snapshot.a);
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

export function upscaleTexturePixels(source,sourceResolution,targetResolution){
  if(sourceResolution===targetResolution)return source;
  const result=new Uint8Array(targetResolution*targetResolution*4);
  for(let y=0;y<targetResolution;y++)for(let x=0;x<targetResolution;x++){
    const sourceX=Math.max(0,Math.min(sourceResolution-1,(x+.5)*sourceResolution/targetResolution-.5));
    const sourceY=Math.max(0,Math.min(sourceResolution-1,(y+.5)*sourceResolution/targetResolution-.5));
    const x0=Math.floor(sourceX),y0=Math.floor(sourceY),x1=Math.min(sourceResolution-1,x0+1),y1=Math.min(sourceResolution-1,y0+1);
    const fx=sourceX-x0,fy=sourceY-y0,to=(y*targetResolution+x)*4;
    for(let channel=0;channel<4;channel++){
      const top=source[(y0*sourceResolution+x0)*4+channel]*(1-fx)+source[(y0*sourceResolution+x1)*4+channel]*fx;
      const bottom=source[(y1*sourceResolution+x0)*4+channel]*(1-fx)+source[(y1*sourceResolution+x1)*4+channel]*fx;
      result[to+channel]=Math.round(top*(1-fy)+bottom*fy);
    }
  }
  return result;
}

export function chunkTexturePixels(chunk,seaLevel){
  return previewTexturePixels({resolution:chunk.width,elevation:chunk.elevationMeters,forest:chunk.forestCover,biome:chunk.biome},seaLevel);
}

export function worldMatrixForGl(matrix){
  return new Float32Array([matrix[0],matrix[3],matrix[6],matrix[1],matrix[4],matrix[7],matrix[2],matrix[5],matrix[8]]);
}

export function viewMatrixForGl(matrix){return new Float32Array(matrix);}

export function snapshotUv(point,matrix,{width,height,centerX,centerY,radius}){
  const view={x:matrix[0]*point.x+matrix[3]*point.y+matrix[6]*point.z,
    y:matrix[1]*point.x+matrix[4]*point.y+matrix[7]*point.z,
    z:matrix[2]*point.x+matrix[5]*point.y+matrix[8]*point.z};
  if(view.z<=0)return null;
  return {x:(centerX+view.x*radius)/width,y:(height-centerY+view.y*radius)/height,z:view.z};
}

export function rankSnapshots(slots,matrix,radius){
  const forward=[matrix[2],matrix[5],matrix[8]];
  const score=item=>item.forward[0]*forward[0]+item.forward[1]*forward[1]+item.forward[2]*forward[2]-.22*Math.abs(Math.log(radius/item.radius))+.000001*item.stamp;
  return [...slots].sort((left,right)=>score(right)-score(left));
}

export class WebGlobeRenderer{
  constructor(canvas){
    this.canvas=canvas;this.available=false;this.frames=0;this.milliseconds=0;
    this.snapshotMatrices=new Float32Array(SNAPSHOT_POOL_SIZE*9);
    this.snapshotViewports=new Float32Array(SNAPSHOT_POOL_SIZE*2);
    this.snapshotCenters=new Float32Array(SNAPSHOT_POOL_SIZE*2);
    this.snapshotRadii=new Float32Array(SNAPSHOT_POOL_SIZE);
    this.worldMatrix=new Float32Array(9);
    try{
      const gl=canvas.getContext("webgl2",{alpha:false,antialias:false,depth:false,stencil:false,preserveDrawingBuffer:false,powerPreference:"high-performance"});
      if(!gl)return;this.gl=gl;this.program=program(gl);this.vao=gl.createVertexArray();
      this.uniforms=Object.fromEntries(["uViewport","uCenter","uRadius","uWorldFromView","uTerrain","uStars","uStarsReady","uSnapshot0","uSnapshot1","uSnapshot2","uSnapshot3","uSnapshotCount","uSeed"].map(name=>[name,gl.getUniformLocation(this.program,name)]));
      this.uniforms.uSnapshotViewFromWorld=gl.getUniformLocation(this.program,"uSnapshotViewFromWorld[0]");
      this.uniforms.uSnapshotViewport=gl.getUniformLocation(this.program,"uSnapshotViewport[0]");
      this.uniforms.uSnapshotCenter=gl.getUniformLocation(this.program,"uSnapshotCenter[0]");
      this.uniforms.uSnapshotRadius=gl.getUniformLocation(this.program,"uSnapshotRadius[0]");
      this.available=true;
    }catch(error){this.error=error;}
  }
  initialize({preview,seaLevel,seed,faceSize=preview.resolution}){
    if(!this.available)return false;
    const gl=this.gl,resolution=faceSize;
    const byFace=new Map(preview.faces.map(face=>[face.face,face]));
    this.texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);
    gl.texStorage3D(gl.TEXTURE_2D_ARRAY,1,gl.RGBA8,resolution,resolution,6);
    for(let layer=0;layer<FACE_ORDER.length;layer++){
      const face=byFace.get(FACE_ORDER[layer]);if(!face)throw new Error(`Нет обзорной текстуры ${FACE_ORDER[layer]}`);
      const overview=previewTexturePixels({...face,resolution:preview.resolution},seaLevel);
      gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,resolution,resolution,1,gl.RGBA,gl.UNSIGNED_BYTE,upscaleTexturePixels(overview,preview.resolution,resolution));
    }
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    this.seed=seed??0;this.seaLevel=seaLevel;this.textureResolution=resolution;this.chunkUploads=0;return true;
  }
  updateChunk(chunk){
    if(!this.available||!this.texture)return false;
    const layer=FACE_ORDER.indexOf(chunk.face);if(layer<0)return false;
    const gl=this.gl;gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT,1);
    gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,chunk.originX,chunk.originY,layer,chunk.width,chunk.height,1,gl.RGBA,gl.UNSIGNED_BYTE,chunkTexturePixels(chunk,this.seaLevel));
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
  async loadExactSurface(url){
    if(!this.available||!this.texture)return false;
    const response=await fetch(url,{cache:'no-store'});if(!response.ok)return false;
    const pixels=new Uint8Array(await response.arrayBuffer()),faceBytes=this.textureResolution*this.textureResolution*4;
    if(pixels.byteLength!==faceBytes*6)throw new Error(`GPU-поверхность имеет неверный размер: ${pixels.byteLength}`);
    const gl=this.gl;gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);
    for(let layer=0;layer<6;layer++)gl.texSubImage3D(gl.TEXTURE_2D_ARRAY,0,0,0,layer,this.textureResolution,this.textureResolution,1,gl.RGBA,gl.UNSIGNED_BYTE,pixels.subarray(layer*faceBytes,(layer+1)*faceBytes));
    this.canvas.dataset.surface='exact';return true;
  }
  captureSnapshot(source,{width,height,centerX,centerY,radius,matrix}){
    if(!this.available||!this.texture||!source)return false;
    const gl=this.gl,forward=[matrix[2],matrix[5],matrix[8]];
    this.snapshotSlots??=[];
    let slot=this.snapshotSlots.find(item=>item.width===width&&item.height===height&&
      item.forward[0]*forward[0]+item.forward[1]*forward[1]+item.forward[2]*forward[2]>.9995&&Math.abs(Math.log(radius/item.radius))<.035);
    if(!slot){
      if(this.snapshotSlots.length<SNAPSHOT_POOL_SIZE){slot={texture:gl.createTexture(),index:this.snapshotSlots.length};this.snapshotSlots.push(slot);}
      else slot=this.snapshotSlots.reduce((oldest,item)=>item.stamp<oldest.stamp?item:oldest);
    }
    gl.bindTexture(gl.TEXTURE_2D,slot.texture);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,true);
    gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,source);gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,false);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);gl.generateMipmap(gl.TEXTURE_2D);
    Object.assign(slot,{width,height,centerX,centerY:height-centerY,radius,matrix:[...matrix],forward,stamp:(this.snapshotStamp??0)+1});
    this.snapshotStamp=slot.stamp;this.snapshotReady=true;this.snapshots=(this.snapshots??0)+1;
    this.canvas.dataset.snapshots=String(this.snapshots);this.canvas.dataset.snapshotPool=String(this.snapshotSlots.length);return true;
  }
  draw({width,height,centerX,centerY,radius,matrix,useSnapshot=false}){
    if(!this.available||!this.texture)return false;
    const started=performance.now(),gl=this.gl;
    if(this.canvas.width!==width)this.canvas.width=width;if(this.canvas.height!==height)this.canvas.height=height;
    gl.viewport(0,0,width,height);gl.disable(gl.DEPTH_TEST);gl.disable(gl.BLEND);gl.useProgram(this.program);gl.bindVertexArray(this.vao);
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D_ARRAY,this.texture);gl.uniform1i(this.uniforms.uTerrain,0);
    gl.activeTexture(gl.TEXTURE1);gl.bindTexture(gl.TEXTURE_2D,this.starTexture??null);gl.uniform1i(this.uniforms.uStars,1);gl.uniform1f(this.uniforms.uStarsReady,this.starsReady?1:0);
    const snapshots=useSnapshot&&this.snapshotReady?rankSnapshots(this.snapshotSlots,matrix,radius):[];
    const snapshotMatrices=this.snapshotMatrices,snapshotViewports=this.snapshotViewports,snapshotCenters=this.snapshotCenters,snapshotRadii=this.snapshotRadii;
    snapshotMatrices.fill(0);snapshotViewports.fill(0);snapshotCenters.fill(0);snapshotRadii.fill(0);
    for(let index=0;index<SNAPSHOT_POOL_SIZE;index++){
      const snapshot=snapshots[index];gl.activeTexture(gl.TEXTURE2+index);gl.bindTexture(gl.TEXTURE_2D,snapshot?.texture??null);gl.uniform1i(this.uniforms[`uSnapshot${index}`],2+index);
      if(!snapshot)continue;snapshotMatrices.set(viewMatrixForGl(snapshot.matrix),index*9);snapshotViewports.set([snapshot.width,snapshot.height],index*2);snapshotCenters.set([snapshot.centerX,snapshot.centerY],index*2);snapshotRadii[index]=snapshot.radius;
    }
    gl.uniform1i(this.uniforms.uSnapshotCount,snapshots.length);gl.uniformMatrix3fv(this.uniforms.uSnapshotViewFromWorld,false,snapshotMatrices);
    gl.uniform2fv(this.uniforms.uSnapshotViewport,snapshotViewports);gl.uniform2fv(this.uniforms.uSnapshotCenter,snapshotCenters);gl.uniform1fv(this.uniforms.uSnapshotRadius,snapshotRadii);
    this.canvas.dataset.snapshotCandidates=String(snapshots.length);
    gl.uniform2f(this.uniforms.uViewport,width,height);gl.uniform2f(this.uniforms.uCenter,centerX,height-centerY);gl.uniform1f(this.uniforms.uRadius,radius);
    const worldMatrix=this.worldMatrix;
    worldMatrix[0]=matrix[0];worldMatrix[1]=matrix[3];worldMatrix[2]=matrix[6];worldMatrix[3]=matrix[1];worldMatrix[4]=matrix[4];worldMatrix[5]=matrix[7];worldMatrix[6]=matrix[2];worldMatrix[7]=matrix[5];worldMatrix[8]=matrix[8];
    gl.uniformMatrix3fv(this.uniforms.uWorldFromView,false,worldMatrix);gl.uniform1f(this.uniforms.uSeed,(this.seed%100000)+1);
    gl.drawArrays(gl.TRIANGLES,0,3);this.frames++;this.milliseconds=performance.now()-started;
    this.canvas.dataset.frames=String(this.frames);this.canvas.dataset.frameMs=this.milliseconds.toFixed(2);return true;
  }
}
