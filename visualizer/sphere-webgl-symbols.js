import {retainedCommitKeys} from './sphere-retained-layer.js';

const INSTANCE_STRIDE=10;

const vertexSource=`#version 300 es
precision highp float;
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aPoint;
layout(location=2) in vec4 aUv;
layout(location=3) in float aSize;
layout(location=4) in float aOpacity;
uniform vec2 uViewport;
uniform vec2 uCenter;
uniform float uRadius;
uniform mat3 uViewFromWorld;
out vec2 vUv;
out float vDepth;
out float vOpacity;
void main(){
  vec3 view=uViewFromWorld*aPoint;
  vec2 screen=uCenter+view.xy*uRadius+aCorner*aSize*.5;
  gl_Position=vec4(screen/uViewport*2.0-1.0,0.0,1.0);
  vUv=mix(aUv.xy,aUv.zw,aCorner*.5+.5);
  vDepth=view.z;
  vOpacity=aOpacity;
}`;

const fragmentSource=`#version 300 es
precision highp float;
uniform sampler2D uAtlas;
in vec2 vUv;
in float vDepth;
in float vOpacity;
out vec4 outColor;
void main(){
  if(vDepth<=.002)discard;
  vec4 color=texture(uAtlas,vUv);
  if(color.a<.015)discard;
  outColor=vec4(color.rgb,color.a*vOpacity*smoothstep(.002,.08,vDepth));
}`;

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

function escapeXml(value){return String(value).replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;').replaceAll('>','&gt;');}

export function symbolAtlasSvg(atlas,{tile=48,columns=8}={}){
  const rows=Math.ceil(atlas.symbols.length/columns),width=columns*tile,height=rows*tile;
  const body=atlas.symbols.map((symbol,index)=>{
    const column=index%columns,row=Math.floor(index/columns),x=column*tile+tile/2,y=row*tile+tile/2;
    const color=atlas.palette[symbol.role]??atlas.palette.ink??'#39443a',dash=symbol.dash?` stroke-dasharray="${symbol.dash.join(' ')}"`:'';
    return `<path d="${escapeXml(symbol.path)}" transform="translate(${x} ${y}) scale(${tile/32})" fill="${symbol.fill?color:'none'}" stroke="${color}" stroke-width="1.65" stroke-linejoin="round" stroke-linecap="round"${dash}/>`;
  }).join('');
  return {svg:`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">${body}</svg>`,width,height,tile,columns,rows};
}

export function symbolInstances(symbols,uvById,pixelRatio=1){
  const data=new Float32Array(symbols.length*INSTANCE_STRIDE);let offset=0;
  for(const symbol of symbols){
    const point=Array.isArray(symbol.point)?symbol.point:[symbol.point.x,symbol.point.y,symbol.point.z],uv=uvById.get(symbol.id);
    if(!uv)continue;
    data.set([point[0],point[1],point[2],uv[0],uv[1],uv[2],uv[3],(symbol.size??17)*pixelRatio,symbol.opacity??1,symbol.rotation??0],offset);
    offset+=INSTANCE_STRIDE;
  }
  return offset===data.length?data:data.slice(0,offset);
}

export class RetainedSphereSymbolLayer{
  constructor(canvas,{capacity=420}={}){
    this.canvas=canvas;this.capacity=capacity;this.entries=new Map();this.activeKeys=[];this.available=false;this.ready=false;this.pixelRatio=1;
    this.builds=0;this.frames=0;this.uploadedBytes=0;
    try{
      const gl=canvas.getContext('webgl2',{alpha:true,antialias:false,depth:false,stencil:false,premultipliedAlpha:true,preserveDrawingBuffer:false,powerPreference:'high-performance'});
      if(!gl)return;this.gl=gl;this.program=makeProgram(gl);this.vao=gl.createVertexArray();this.quadBuffer=gl.createBuffer();this.activeBuffer=gl.createBuffer();this.patchBuffer=gl.createBuffer();
      this.uniforms=Object.fromEntries(['uViewport','uCenter','uRadius','uViewFromWorld','uAtlas'].map(name=>[name,gl.getUniformLocation(this.program,name)]));
      gl.bindVertexArray(this.vao);gl.bindBuffer(gl.ARRAY_BUFFER,this.quadBuffer);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,-1,1,1,1]),gl.STATIC_DRAW);
      gl.enableVertexAttribArray(0);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);this.available=true;
    }catch(error){this.error=error;canvas.dataset.error=String(error?.message??error);}
  }
  initialize(atlas){
    if(!this.available||typeof Image==='undefined')return Promise.resolve(false);
    const gl=this.gl,layout=symbolAtlasSvg(atlas);
    this.uvById=new Map(atlas.symbols.map((symbol,index)=>{const column=index%layout.columns,row=Math.floor(index/layout.columns);
      const inset=2/layout.width,insetY=2/layout.height;
      // DOM images are uploaded top row first. The screen quad starts at its
      // lower edge, therefore V intentionally runs from the tile bottom to top.
      return [symbol.id,[column/layout.columns+inset,(row+1)/layout.rows-insetY,(column+1)/layout.columns-inset,row/layout.rows+insetY]];}));
    const image=new Image();image.decoding='async';image.src=`data:image/svg+xml;charset=utf-8,${encodeURIComponent(layout.svg)}`;
    return image.decode().then(()=>{
      this.texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,this.texture);gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL,true);
      gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,image);gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL,false);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);gl.generateMipmap(gl.TEXTURE_2D);
      this.ready=true;this.canvas.dataset.atlas=`${atlas.symbols.length}`;return true;
    }).catch(error=>{this.canvas.dataset.error=`Не удалось собрать GPU-атлас знаков: ${error?.message??error}`;return false;});
  }
  resize(width,height,pixelRatio=1){
    this.pixelRatio=pixelRatio;const w=Math.max(1,Math.round(width*pixelRatio)),h=Math.max(1,Math.round(height*pixelRatio));
    if(this.canvas.width!==w)this.canvas.width=w;if(this.canvas.height!==h)this.canvas.height=h;this.canvas.style.width=`${width}px`;this.canvas.style.height=`${height}px`;
  }
  retain(key,version,build){
    if(!this.ready)return false;let entry=this.entries.get(key);
    if(!entry||entry.version!==version||entry.pixelRatio!==this.pixelRatio){
      if(entry)this.uploadedBytes-=entry.bytes;const data=symbolInstances(build(),this.uvById,this.pixelRatio);
      entry={key,version,pixelRatio:this.pixelRatio,data,count:data.length/INSTANCE_STRIDE,bytes:data.byteLength,stamp:0};this.entries.set(key,entry);this.builds++;this.uploadedBytes+=entry.bytes;
    }
    entry.stamp=performance.now();return true;
  }
  commit(keys,{incremental=false}={}){
    if(!this.ready)return;const exactDesired=[...new Set(keys)].filter(key=>this.entries.has(key));
    const replacesBase=exactDesired.some(key=>this.baseVersions?.has(key)&&this.baseVersions.get(key)!==this.entries.get(key).version);
    const desired=retainedCommitKeys(this.baseKeys,exactDesired,{incremental,replacesBase});const protectedKeys=new Set([...(this.baseKeys??[]),...desired]);
    while(this.entries.size>this.capacity){const candidate=[...this.entries.values()].filter(entry=>!protectedKeys.has(entry.key)).sort((a,b)=>a.stamp-b.stamp)[0];if(!candidate)break;this.entries.delete(candidate.key);this.uploadedBytes-=candidate.bytes;}
    if(incremental&&this.activeSignature&&!replacesBase){const patchKeys=desired.filter(key=>!this.baseVersions?.has(key)||this.baseVersions.get(key)!==this.entries.get(key).version);
      const patchSignature=patchKeys.map(key=>{const entry=this.entries.get(key);return `${key}@${entry.version}@${entry.pixelRatio}`;}).join('|');
      if(patchSignature!==this.patchSignature){const entries=patchKeys.map(key=>this.entries.get(key)),length=entries.reduce((sum,entry)=>sum+entry.data.length,0),packed=new Float32Array(length);let offset=0;
        for(const entry of entries){packed.set(entry.data,offset);offset+=entry.data.length;}this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.patchBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,packed,this.gl.DYNAMIC_DRAW);
        this.patchSignature=patchSignature;this.patchKeys=patchKeys;this.patchCount=packed.length/INSTANCE_STRIDE;this.patchBytes=packed.byteLength;this.patchUploads=(this.patchUploads??0)+1;}
      this.activeKeys=[...new Set([...(this.baseKeys??[]),...patchKeys])];this.status();return;}
    this.activeKeys=desired;
    const signature=this.activeKeys.map(key=>{const entry=this.entries.get(key);return `${key}@${entry.version}@${entry.pixelRatio}`;}).join('|');
    if(signature!==this.activeSignature){const entries=this.activeKeys.map(key=>this.entries.get(key)),length=entries.reduce((sum,entry)=>sum+entry.data.length,0),packed=new Float32Array(length);let offset=0;
      for(const entry of entries){packed.set(entry.data,offset);offset+=entry.data.length;}this.gl.bindBuffer(this.gl.ARRAY_BUFFER,this.activeBuffer);this.gl.bufferData(this.gl.ARRAY_BUFFER,packed,this.gl.STATIC_DRAW);
      this.activeSignature=signature;this.activeCount=packed.length/INSTANCE_STRIDE;this.activeBytes=packed.byteLength;this.uploads=(this.uploads??0)+1;
    }this.baseKeys=new Set(this.activeKeys);this.baseVersions=new Map(this.activeKeys.map(key=>[key,this.entries.get(key).version]));
    this.patchKeys=[];this.patchSignature='';this.patchCount=0;this.patchBytes=0;this.status();
  }
  draw({width,height,centerX,centerY,radius,matrix,enabled=true}){
    if(!this.available)return false;const gl=this.gl;gl.viewport(0,0,width,height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
    if(!enabled||!this.ready||(!this.activeCount&&!this.patchCount))return true;gl.disable(gl.DEPTH_TEST);gl.enable(gl.BLEND);gl.blendFunc(gl.ONE,gl.ONE_MINUS_SRC_ALPHA);gl.useProgram(this.program);gl.bindVertexArray(this.vao);
    gl.uniform2f(this.uniforms.uViewport,width,height);gl.uniform2f(this.uniforms.uCenter,centerX,height-centerY);gl.uniform1f(this.uniforms.uRadius,radius);gl.uniformMatrix3fv(this.uniforms.uViewFromWorld,false,new Float32Array(matrix));
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,this.texture);gl.uniform1i(this.uniforms.uAtlas,0);gl.bindBuffer(gl.ARRAY_BUFFER,this.activeBuffer);const stride=INSTANCE_STRIDE*4;
    const attribute=(location,size,offset)=>{gl.enableVertexAttribArray(location);gl.vertexAttribPointer(location,size,gl.FLOAT,false,stride,offset*4);gl.vertexAttribDivisor(location,1);};
    const drawBuffer=(buffer,count)=>{if(!count)return;gl.bindBuffer(gl.ARRAY_BUFFER,buffer);attribute(1,3,0);attribute(2,4,3);attribute(3,1,7);attribute(4,1,8);gl.drawArraysInstanced(gl.TRIANGLE_STRIP,0,4,count);};
    drawBuffer(this.activeBuffer,this.activeCount);drawBuffer(this.patchBuffer,this.patchCount);this.frames++;this.status();return true;
  }
  status(){this.canvas.dataset.builds=String(this.builds);this.canvas.dataset.frames=String(this.frames);this.canvas.dataset.instances=String((this.activeCount??0)+(this.patchCount??0));this.canvas.dataset.bytes=String(this.uploadedBytes);this.canvas.dataset.activeBytes=String(this.activeBytes??0);this.canvas.dataset.patchBytes=String(this.patchBytes??0);this.canvas.dataset.uploads=String(this.uploads??0);this.canvas.dataset.patchUploads=String(this.patchUploads??0);this.canvas.dataset.drawCalls=String((this.activeCount?1:0)+(this.patchCount?1:0));}
}
