import {partitionRetainedEntries,retainedCommitKeys,retainedTransitionNeeded} from './sphere-retained-layer.js';

const VERTEX_STRIDE=17;

const vertexSource=`#version 300 es
precision highp float;
layout(location=0) in vec3 aPoint;
layout(location=1) in vec3 aOther;
layout(location=2) in float aEndpoint;
layout(location=3) in float aSide;
layout(location=4) in float aWidth;
layout(location=5) in vec4 aColor;
uniform vec2 uViewport;
uniform vec2 uCenter;
uniform float uRadius;
uniform float uOpacity;
uniform mat3 uViewFromWorld;
out float vEdge;
out float vDepth;
out vec4 vColor;
layout(location=6) in float aDistance;
layout(location=7) in vec2 aDash;
layout(location=8) in float aOffset;
out float vDistance;
out vec2 vDash;
void main(){
  vec3 pointView=uViewFromWorld*aPoint;
  vec3 otherView=uViewFromWorld*aOther;
  vec2 tangent=normalize((otherView.xy-pointView.xy)*(aEndpoint<.5?1.0:-1.0));
  if(any(isnan(tangent)))tangent=vec2(1.0,0.0);
  vec2 normal=vec2(-tangent.y,tangent.x);
  vec2 screen=uCenter+pointView.xy*uRadius+normal*(aSide*(aWidth*.5+1.0)+aOffset);
  vec2 clip=screen/uViewport*2.0-1.0;
  gl_Position=vec4(clip,0.0,1.0);
  vEdge=aSide;
  vDepth=pointView.z;
  vColor=aColor;
  vDistance=aDistance*uRadius;
  vDash=aDash;
}`;

const fragmentSource=`#version 300 es
precision highp float;
uniform float uOpacity;
in float vEdge;
in float vDepth;
in vec4 vColor;
in float vDistance;
in vec2 vDash;
out vec4 outColor;
void main(){
  if(vDepth<=.002)discard;
  if(vDash.x>0.0&&mod(vDistance,vDash.x)>vDash.y)discard;
  float coverage=1.0-smoothstep(.64,1.0,abs(vEdge));
  outColor=vec4(vColor.rgb,vColor.a*coverage*uOpacity);
}`;

export {fragmentSource as sphereLineFragmentSource};

function compile(gl,type,source){
  const shader=gl.createShader(type);gl.shaderSource(shader,source);gl.compileShader(shader);
  if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)){const message=gl.getShaderInfoLog(shader);gl.deleteShader(shader);throw new Error(message);}
  return shader;
}

function makeProgram(gl){
  const result=gl.createProgram(),vertex=compile(gl,gl.VERTEX_SHADER,vertexSource),fragment=compile(gl,gl.FRAGMENT_SHADER,fragmentSource);
  gl.attachShader(result,vertex);gl.attachShader(result,fragment);gl.linkProgram(result);gl.deleteShader(vertex);gl.deleteShader(fragment);
  if(!gl.getProgramParameter(result,gl.LINK_STATUS)){const message=gl.getProgramInfoLog(result);gl.deleteProgram(result);throw new Error(message);}
  return result;
}

function vector(point){return Array.isArray(point)?point:[point.x,point.y,point.z];}

export function parseLineColor(value,alpha=1){
  const text=String(value??""),hex=text.startsWith("#")?text.match(/[0-9a-f]{2}/gi):null;
  if(hex?.length>=3)return [parseInt(hex[0],16)/255,parseInt(hex[1],16)/255,parseInt(hex[2],16)/255,alpha];
  const rgb=text.match(/[\d.]+/g)?.slice(0,3).map(Number);
  return rgb?.length===3?[rgb[0]/255,rgb[1]/255,rgb[2]/255,alpha]:[.3,.3,.3,alpha];
}

export function lineVertexData(lines,pixelRatio=1){
  let segmentCount=0;
  for(const line of lines)segmentCount+=Math.max(0,line.points.length-1);
  const data=new Float32Array(segmentCount*6*VERTEX_STRIDE);let offset=0;
  const write=(point,other,endpoint,side,width,color,distance,dash,lineOffset)=>{
    const p=vector(point),q=vector(other);
    data.set([p[0],p[1],p[2],q[0],q[1],q[2],endpoint,side,width*pixelRatio,...color,distance,dash[0]*pixelRatio,dash[1]*pixelRatio,lineOffset*pixelRatio],offset);
    offset+=VERTEX_STRIDE;
  };
  for(const line of lines){
    const color=parseLineColor(line.color,line.alpha??1),width=line.width??1,lineOffset=line.offset??0,
      dash=line.dash?.length?[line.dash.reduce((sum,value)=>sum+value,0),line.dash[0]]:[0,0];
    let distance=0;
    for(let index=1;index<line.points.length;index++){
      const a=line.points[index-1],b=line.points[index];
      const p=vector(a),q=vector(b),dot=Math.max(-1,Math.min(1,p[0]*q[0]+p[1]*q[1]+p[2]*q[2])),next=distance+Math.acos(dot);
      write(a,b,0,-1,width,color,distance,dash,lineOffset);write(a,b,0,1,width,color,distance,dash,lineOffset);write(b,a,1,-1,width,color,next,dash,lineOffset);
      write(b,a,1,-1,width,color,next,dash,lineOffset);write(a,b,0,1,width,color,distance,dash,lineOffset);write(b,a,1,1,width,color,next,dash,lineOffset);
      distance=next;
    }
  }
  return data;
}

// Contours are retained in world coordinates. Camera motion changes only four
// uniforms; geometry is rebuilt solely when its tile version or LOD changes.
export class RetainedSphereLineLayer{
  constructor(canvas,{capacity=320}={}){
    this.canvas=canvas;this.capacity=capacity;this.entries=new Map();this.activeKeys=[];this.available=false;
    this.builds=0;this.frames=0;this.uploadedBytes=0;this.pixelRatio=1;
    try{
      const gl=canvas.getContext("webgl2",{alpha:true,antialias:false,depth:false,stencil:false,premultipliedAlpha:true,preserveDrawingBuffer:false,powerPreference:"high-performance"});
      if(!gl)return;this.gl=gl;this.program=makeProgram(gl);this.vao=gl.createVertexArray();
      this.activeBuffer=gl.createBuffer();this.animatedBuffer=gl.createBuffer();this.patchBuffer=gl.createBuffer();this.transitionBuffer=gl.createBuffer();
      this.uniforms=Object.fromEntries(["uViewport","uCenter","uRadius","uOpacity","uViewFromWorld"].map(name=>[name,gl.getUniformLocation(this.program,name)]));
      gl.bindVertexArray(this.vao);this.available=true;
    }catch(error){this.error=error;this.canvas.dataset.error=String(error?.message??error);}
  }
  resize(width,height,pixelRatio=1){
    this.pixelRatio=pixelRatio;const w=Math.max(1,Math.round(width*pixelRatio)),h=Math.max(1,Math.round(height*pixelRatio));
    if(this.canvas.width!==w)this.canvas.width=w;if(this.canvas.height!==h)this.canvas.height=h;
    this.canvas.style.width=`${width}px`;this.canvas.style.height=`${height}px`;
  }
  retain(key,version,build,{animate=false}={}){
    if(!this.available)return false;
    let entry=this.entries.get(key);
    if(!entry||entry.version!==version||entry.pixelRatio!==this.pixelRatio){
      if(entry)this.remove(entry);
      const data=lineVertexData(build(),this.pixelRatio);
      entry={key,version,pixelRatio:this.pixelRatio,data,count:data.length/VERTEX_STRIDE,bytes:data.byteLength,stamp:0,animate};
      this.entries.set(key,entry);this.builds++;this.uploadedBytes+=entry.bytes;
    }else entry.animate=animate;
    entry.stamp=performance.now();return true;
  }
  commit(keys,{incremental=false}={}){
    const exactDesired=[...new Set(keys)].filter(key=>this.entries.has(key));
    const replacesBase=exactDesired.some(key=>this.baseVersions?.has(key)&&this.baseVersions.get(key)!==this.entries.get(key).version);
    const animatesBase=exactDesired.some(key=>this.entries.get(key).animate&&this.baseVersions?.has(key)&&this.baseVersions.get(key)!==this.entries.get(key).version);
    const desired=retainedCommitKeys(this.baseKeys,exactDesired,{incremental,replacesBase});
    const protectedKeys=new Set([...(this.baseKeys??[]),...desired]);
    while(this.entries.size>this.capacity){
      const candidate=[...this.entries.values()].filter(entry=>!protectedKeys.has(entry.key)).sort((a,b)=>a.stamp-b.stamp)[0];
      if(!candidate)break;this.entries.delete(candidate.key);this.remove(candidate);
    }
    if(incremental&&this.activeSignature&&!replacesBase){
      const patchKeys=desired.filter(key=>!this.baseVersions?.has(key)||this.baseVersions.get(key)!==this.entries.get(key).version);
      const patchSignature=patchKeys.map(key=>{const entry=this.entries.get(key);return `${key}@${entry.version}@${entry.pixelRatio}`;}).join('|');
      if(patchSignature!==this.patchSignature){
        const entries=patchKeys.map(key=>this.entries.get(key)),length=entries.reduce((sum,entry)=>sum+entry.data.length,0),packed=new Float32Array(length);let offset=0;
        for(const entry of entries){packed.set(entry.data,offset);offset+=entry.data.length;}
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.patchBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,packed,this.gl.DYNAMIC_DRAW);
        this.patchPacked=packed;this.patchSignature=patchSignature;this.patchKeys=patchKeys;this.patchCount=packed.length/VERTEX_STRIDE;this.patchBytes=packed.byteLength;this.patchUploads=(this.patchUploads??0)+1;
      }
      this.activeKeys=[...new Set([...(this.baseKeys??[]),...patchKeys])];this.status();return;
    }
    this.activeKeys=desired;
    const signature=this.activeKeys.map(key=>{const entry=this.entries.get(key);return `${key}@${entry.version}@${entry.pixelRatio}`;}).join("|");
    if(signature!==this.activeSignature){
      const entries=this.activeKeys.map(key=>this.entries.get(key)),parts=partitionRetainedEntries(entries);
      const pack=items=>{const length=items.reduce((sum,entry)=>sum+entry.data.length,0),result=new Float32Array(length);let offset=0;
        for(const entry of items){result.set(entry.data,offset);offset+=entry.data.length;}return result;};
      const fixedPacked=pack(parts.fixed),animatedPacked=pack(parts.animated),oldAnimatedLength=this.activeAnimatedPacked?.length??0;
      if(retainedTransitionNeeded(oldAnimatedLength,animatesBase)){
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.transitionBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,this.activeAnimatedPacked,this.gl.DYNAMIC_DRAW);
        this.transitionCount=oldAnimatedLength/VERTEX_STRIDE;this.transitionStarted=performance.now();
      }else{this.transitionCount=0;this.transitionStarted=undefined;}
      this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.activeBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,fixedPacked,this.gl.STATIC_DRAW);
      this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.animatedBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,animatedPacked,this.gl.DYNAMIC_DRAW);
      this.activeFixedPacked=fixedPacked;this.activeAnimatedPacked=animatedPacked;this.activeSignature=signature;
      this.activeCount=fixedPacked.length/VERTEX_STRIDE;this.animatedCount=animatedPacked.length/VERTEX_STRIDE;
      this.activeBytes=fixedPacked.byteLength+animatedPacked.byteLength;this.activeUploads=(this.activeUploads??0)+1;
    }
    this.baseKeys=new Set(this.activeKeys);this.baseVersions=new Map(this.activeKeys.map(key=>[key,this.entries.get(key).version]));
    this.patchKeys=[];this.patchPacked=null;this.patchSignature='';this.patchCount=0;this.patchBytes=0;
    this.status();
  }
  remove(entry){
    this.uploadedBytes=Math.max(0,this.uploadedBytes-entry.bytes);
  }
  clear(){
    if(!this.available)return;const gl=this.gl;gl.viewport(0,0,this.canvas.width,this.canvas.height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
  }
  draw({width,height,centerX,centerY,radius,matrix,enabled=true}){
    if(!this.available)return false;
    const gl=this.gl;gl.viewport(0,0,width,height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
    if(!enabled||!this.activeKeys.length)return true;
    gl.disable(gl.DEPTH_TEST);gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.useProgram(this.program);gl.bindVertexArray(this.vao);
    gl.uniform2f(this.uniforms.uViewport,width,height);gl.uniform2f(this.uniforms.uCenter,centerX,height-centerY);gl.uniform1f(this.uniforms.uRadius,radius);
    gl.uniformMatrix3fv(this.uniforms.uViewFromWorld,false,new Float32Array(matrix));
    const stride=VERTEX_STRIDE*4,drawBuffer=(buffer,count,opacity=1)=>{if(!count||opacity<=.001)return;gl.uniform1f(this.uniforms.uOpacity,opacity);gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
      gl.enableVertexAttribArray(0);gl.vertexAttribPointer(0,3,gl.FLOAT,false,stride,0);
      gl.enableVertexAttribArray(1);gl.vertexAttribPointer(1,3,gl.FLOAT,false,stride,3*4);
      gl.enableVertexAttribArray(2);gl.vertexAttribPointer(2,1,gl.FLOAT,false,stride,6*4);
      gl.enableVertexAttribArray(3);gl.vertexAttribPointer(3,1,gl.FLOAT,false,stride,7*4);
      gl.enableVertexAttribArray(4);gl.vertexAttribPointer(4,1,gl.FLOAT,false,stride,8*4);
      gl.enableVertexAttribArray(5);gl.vertexAttribPointer(5,4,gl.FLOAT,false,stride,9*4);
      gl.enableVertexAttribArray(6);gl.vertexAttribPointer(6,1,gl.FLOAT,false,stride,13*4);
      gl.enableVertexAttribArray(7);gl.vertexAttribPointer(7,2,gl.FLOAT,false,stride,14*4);
      gl.enableVertexAttribArray(8);gl.vertexAttribPointer(8,1,gl.FLOAT,false,stride,16*4);gl.drawArrays(gl.TRIANGLES,0,count);};
    const transition=this.transitionStarted===undefined?1:Math.min(1,(performance.now()-this.transitionStarted)/900);
    if(transition<1)drawBuffer(this.transitionBuffer,this.transitionCount??0,1-transition);
    else this.transitionCount=0;
    drawBuffer(this.animatedBuffer,this.animatedCount??0,transition);drawBuffer(this.activeBuffer,this.activeCount??0,1);drawBuffer(this.patchBuffer,this.patchCount??0,1);
    this.frames++;this.status();return true;
  }
  status(){
    this.canvas.dataset.builds=String(this.builds);this.canvas.dataset.frames=String(this.frames);
    this.canvas.dataset.tiles=String(this.activeKeys.length);this.canvas.dataset.bytes=String(this.uploadedBytes);
    this.canvas.dataset.activeBytes=String(this.activeBytes??0);this.canvas.dataset.fixedVertices=String(this.activeCount??0);this.canvas.dataset.animatedVertices=String(this.animatedCount??0);this.canvas.dataset.patchBytes=String(this.patchBytes??0);
    this.canvas.dataset.uploads=String(this.activeUploads??0);this.canvas.dataset.patchUploads=String(this.patchUploads??0);
    this.canvas.dataset.drawCalls=String((this.activeCount?1:0)+(this.animatedCount?1:0)+(this.patchCount?1:0));
  }
  get transitioning(){return !!this.transitionCount&&performance.now()-(this.transitionStarted??0)<900;}
}
