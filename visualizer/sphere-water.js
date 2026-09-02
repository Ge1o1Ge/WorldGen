import {facePoint, locateFace, blend} from "./sphere-cartography.js";

// One signed byte is shared by the GPU fill and vector coastline. Two codes
// per metre preserve sub-cell intersections while retaining +/-64 m around a
// bank; sign-changing neighbours normally lie well inside that interval.
export function waterShoreByte(shore){
  const encoded=Math.max(0,Math.min(255,Math.round(128+Math.max(-64,Math.min(63.5,shore))*2)));
  // Code 128 is exactly zero. Never let quantisation erase the wet/dry sign:
  // the renderer needs an actual sign change to choose a shoreline turn from
  // the four terrain-relative corner heights.
  return shore>0?Math.max(129,encoded):Math.min(127,encoded);
}

// The simulation classifies each microcell with its containing hydrology cell.
// Interpolating coarse *depths* expands a deep lake far onto dry land. Interpolate
// bounded wet/dry coverage on the microcell lattice instead, independent of LOD.
// The returned display depth keeps the existing water threshold (> 1 m), while
// preserving actual depth at sample centers and on the lake's interior.
export function createLakeSurfaceSampler({faceSize, resolution, readDepth, readShore}) {
  if (!(faceSize > 0 && resolution > 0 && resolution <= faceSize)) throw new Error("Invalid hydrology grid");
  const ratio = resolution / faceSize;
  const bounded = n => Math.max(0, Math.min(resolution - 1, n));
  function at(face, x, y, read=readDepth) {
    if (x < 0 || y < 0 || x >= faceSize || y >= faceSize) {
      const adjacent = locateFace(facePoint(face, x, y, faceSize));
      return read(adjacent.face, bounded(Math.floor((adjacent.u + 1) * resolution / 2)),
        bounded(Math.floor((adjacent.v + 1) * resolution / 2)));
    }
    return read(face, bounded(Math.floor((x + .5) * ratio)), bounded(Math.floor((y + .5) * ratio)));
  }
  const surface = location => {
    const gx = (location.u + 1) * faceSize / 2 - .5, gy = (location.v + 1) * faceSize / 2 - .5;
    const x = Math.floor(gx), y = Math.floor(gy), tx = gx - x, ty = gy - y;
    if(readShore){
      // Exact hydrology supplies signed height below the neighboring lake level
      // on BOTH sides of the bank. Bilinear interpolation then follows terrain,
      // like a sea-level contour, rather than rounding a binary grid of squares.
      const value=blend([at(location.face,x,y,readShore),at(location.face,x+1,y,readShore),
        at(location.face,x+1,y+1,readShore),at(location.face,x,y+1,readShore)],tx,ty);
      const shore=Math.abs(value)<1e-9?0:value; // Projection round-off must not wet a threshold centre.
      return {shore,depth:Math.max(0,shore+1),coverage:shore>0?1:0};
    }
    const a = at(location.face, x, y), b = at(location.face, x + 1, y),
      c = at(location.face, x + 1, y + 1), d = at(location.face, x, y + 1);
    if (a === b && b === c && c === d) return {depth:a,coverage:a>1?1:0,shore:a>1?1:-1};
    // C1 interpolation rounds corners without moving any microcell centre.
    // Unlike interpolated metres, this field has the same range on both shores.
    const smooth = t => t*t*(3-2*t);
    const coverage = blend([a > 1 ? 1 : 0, b > 1 ? 1 : 0, c > 1 ? 1 : 0, d > 1 ? 1 : 0], smooth(tx), smooth(ty));
    // A deep neighbor can affect the colour of water, never the position of land.
    const depth = coverage <= .5 ? coverage * 2 :
      1 + (coverage * 2 - 1) * Math.max(1, blend([a, b, c, d], tx, ty) - 1);
    return {depth,coverage,shore:coverage*2-1};
  };
  const depth = location => surface(location).depth;
  depth.surface = surface;
  return depth;
}
