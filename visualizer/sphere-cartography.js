// Pure geometry: no browser globals, camera state, or random placement tied to the viewport.
export const FACE_NAMES = ["PositiveX", "NegativeX", "PositiveY", "NegativeY", "PositiveZ", "NegativeZ"];
export function facePoint(face, x, y, size) {
  const u = -1 + (x + 0.5) * 2 / size;
  const v = -1 + (y + 0.5) * 2 / size;
  const point = face === "PositiveX" ? [1,v,-u] : face === "NegativeX" ? [-1,v,u] :
    face === "PositiveY" ? [u,1,-v] : face === "NegativeY" ? [u,-1,v] :
    face === "PositiveZ" ? [u,v,1] : [-u,v,-1];
  const length = Math.hypot(...point);
  return {x:point[0]/length, y:point[1]/length, z:point[2]/length};
}
export function locateFace(point) {
  const ax = Math.abs(point.x), ay = Math.abs(point.y), az = Math.abs(point.z);
  if (ax >= ay && ax >= az) return point.x >= 0
    ? {face:"PositiveX",u:-point.z/ax,v:point.y/ax} : {face:"NegativeX",u:point.z/ax,v:point.y/ax};
  if (ay >= az) return point.y >= 0
    ? {face:"PositiveY",u:point.x/ay,v:-point.z/ay} : {face:"NegativeY",u:point.x/ay,v:point.z/ay};
  return point.z >= 0 ? {face:"PositiveZ",u:point.x/az,v:point.y/az} : {face:"NegativeZ",u:-point.x/az,v:point.y/az};
}

export function blend(values, tx, ty) {
  return values[0]*(1-tx)*(1-ty) + values[1]*tx*(1-ty) + values[2]*tx*ty + values[3]*(1-tx)*ty;
}

// Read may reproject ghost cells across cube seams. The cache lives for one rendered
// frame, so newly arrived chunks cannot leave stale interpolated patches behind.
export function createSurfaceSampler({size, stride, origin, read}) {
  const faces = new Map(FACE_NAMES.map(face=>[face,new Map()]));
  const rowSize=Math.ceil(size/stride)+4;
  const coefficients=values=>[values[0],values[1]-values[0],values[3]-values[0],values[2]-values[3]-values[1]+values[0]];
  const evaluate=(c,x,y)=>c[0]+c[1]*x+c[2]*y+c[3]*x*y;
  return (location, includeClaims = false) => {
    const gx = (((location.u+1)*size/2 - 0.5) - origin) / stride;
    const gy = (((location.v+1)*size/2 - 0.5) - origin) / stride;
    const ix = Math.floor(gx), iy = Math.floor(gy), tx = gx-ix, ty = gy-iy;
    const patches=faces.get(location.face);
    const key = (iy+2)*rowSize+ix+2;
    let patch = patches.get(key);
    if (!patch) {
      const cells = [[0,0],[1,0],[1,1],[0,1]].map(([dx,dy]) => read(location.face, (ix+dx)*stride+origin, (iy+dy)*stride+origin));
      patch={cells,exact:cells.every(item=>item.exact)};
      for(const field of ["elevation","forest","moisture","lakeDepth","temperature"]) patch[field]=coefficients(cells.map(item=>item[field]??0));
      patches.set(key, patch);
    }
    const nearest = patch.cells[ty < .5 ? (tx < .5 ? 0 : 1) : (tx < .5 ? 3 : 2)];
    const result = {biome:nearest.biome, owner:nearest.owner, elevation:evaluate(patch.elevation,tx,ty),
      forest:evaluate(patch.forest,tx,ty),moisture:evaluate(patch.moisture,tx,ty),
      lakeDepth:evaluate(patch.lakeDepth,tx,ty),exact:patch.exact};
    result.temperature=evaluate(patch.temperature,tx,ty);
    if(includeClaims) {
      result.claims=new Map();
      const weights=[(1-tx)*(1-ty),tx*(1-ty),tx*ty,(1-tx)*ty];
      for(let i=0;i<4;i++) {
        const owner=patch.cells[i].owner;
        if(owner>=0) result.claims.set(owner,(result.claims.get(owner)??0)+weights[i]);
      }
    }
    return result;
  };
}

// Marching squares with a bilinear asymptotic decider for saddle cells.
export function contourSegments(values, width, height, threshold, step = 1) {
  const segments = [];
  for (let y=0;y<height-1;y++) for (let x=0;x<width-1;x++) {
    const v = [values[y*width+x],values[y*width+x+1],values[(y+1)*width+x+1],values[(y+1)*width+x]];
    if (!v.every(Number.isFinite)) continue;
    const p = [[x*step,y*step],[(x+1)*step,y*step],[(x+1)*step,(y+1)*step],[x*step,(y+1)*step]];
    const crossing = new Map();
    for (let edge=0;edge<4;edge++) {
      const next=(edge+1)%4;
      if ((v[edge]>=threshold)===(v[next]>=threshold)) continue;
      const t=(threshold-v[edge])/(v[next]-v[edge]);
      crossing.set(edge,[p[edge][0]+(p[next][0]-p[edge][0])*t,p[edge][1]+(p[next][1]-p[edge][1])*t]);
    }
    if (crossing.size===2) segments.push([...crossing.values()]);
    else if (crossing.size===4) {
      const q=(v[0]-threshold)*(v[2]-threshold)-(v[1]-threshold)*(v[3]-threshold);
      const pairs=q>=0 ? [[0,1],[2,3]] : [[0,3],[1,2]];
      for(const [a,b] of pairs) segments.push([crossing.get(a),crossing.get(b)]);
    }
  }
  return segments;
}

export function joinSegments(segments) {
  const key = point => `${Math.round(point[0]*10000)},${Math.round(point[1]*10000)}`;
  const adjacency = new Map();
  segments.forEach((segment,id) => segment.forEach(point => {
    const k=key(point);
    if (!adjacency.has(k)) adjacency.set(k,[]);
    adjacency.get(k).push(id);
  }));
  const used=new Set(), paths=[];
  function walk(id,start) {
    const path=[start];
    let current=start;
    while(id!==undefined && !used.has(id)) {
      used.add(id);
      const segment=segments[id];
      current=key(segment[0])===key(current) ? segment[1] : segment[0];
      path.push(current);
      id=adjacency.get(key(current)).find(next=>!used.has(next));
    }
    paths.push(path);
  }
  segments.forEach((segment,id) => {
    const end=segment.find(point=>adjacency.get(key(point)).length===1);
    if(end && !used.has(id)) walk(id,end);
  });
  segments.forEach((segment,id)=>{ if(!used.has(id)) walk(id,segment[0]); });
  return paths;
}

// Endpoint-preserving rounding, used for coastlines, contours, borders and rivers.
export function smoothPath(context, points) {
  if(points.length<2) return;
  context.moveTo(...points[0]);
  for(let i=1;i<points.length-1;i++) context.quadraticCurveTo(...points[i],
    (points[i][0]+points[i+1][0])/2,(points[i][1]+points[i+1][1])/2);
  context.lineTo(...points.at(-1));
}

// Clip interpolated triangles against a scalar threshold. Used to stop river ink
// precisely at displayed shores, without clipping whole world cells into squares.
export function belowThresholdPolygon(vertices, threshold = 0) {
  const result=[];
  for(let i=0;i<vertices.length;i++) {
    const a=vertices[i],b=vertices[(i+1)%vertices.length];
    if(a.value<=threshold) result.push([a.x,a.y]);
    if((a.value<=threshold)!==(b.value<=threshold)) {
      const t=(threshold-a.value)/(b.value-a.value);
      result.push([a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t]);
    }
  }
  return result;
}

export function symbolSpacing(zoom) {
  return zoom<2 ? 32 : zoom<4 ? 16 : zoom<8 ? 8 : zoom<16 ? 4 : zoom<32 ? 2 : 1;
}
export function symbolAnchor(face, gridX, gridY, spacing, seed) {
  function noise(salt) {
    let hash=(seed ^ Math.imul(gridX+1,73856093) ^ Math.imul(gridY+1,19349663) ^
      Math.imul(FACE_NAMES.indexOf(face)+1,83492791) ^ salt) >>> 0;
    hash=Math.imul(hash^(hash>>>16),0x7feb352d);
    hash=Math.imul(hash^(hash>>>15),0x846ca68b);
    return ((hash^(hash>>>16))>>>0)/4294967296;
  }
  return {face,x:(gridX+.5+(noise(17)-.5)*.35)*spacing-.5,
    y:(gridY+.5+(noise(43)-.5)*.35)*spacing-.5,variant:noise(89)};
}

export function contourInterval(zoom) { return zoom<16 ? 100 : zoom<32 ? 20 : 10; }
