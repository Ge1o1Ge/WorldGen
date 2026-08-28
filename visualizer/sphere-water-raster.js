// One implicit shoreline for colour, land-ink clipping and the shoreline stroke.
// Fields are sampled in world coordinates; screen-space derivatives only control
// antialiasing and the constant CSS width of the stroke, never the world shape.
const clamp01 = value => Math.max(0,Math.min(1,value));
function derivative(value,a,b,step) {
  if(Number.isFinite(a)&&Number.isFinite(b))return (b-a)/(2*step);
  if(Number.isFinite(a))return (value-a)/step;
  if(Number.isFinite(b))return (b-value)/step;
  return 0;
}

function distanceAt(field,index,x,y,width,height,step) {
  const value=field[index];
  if(!Number.isFinite(value))return -Infinity;
  const dx=derivative(value,field[x>=step?index-step:-1],field[x+step<width?index+step:-1],step);
  const dy=derivative(value,field[y>=step?index-step*width:-1],field[y+step<height?index+step*width:-1],step);
  const gradient=Math.hypot(dx,dy);
  // A constant water/land plateau is not a coastline, even at sea level.
  return gradient>1e-12?value/gradient:value>=0?Infinity:-Infinity;
}

export function paintWaterRaster({pixels,mask,ocean,lakes,waterColors,width,height,
  step=1,x0=0,y0=0,x1=width-1,y1=height-1,pixelRatio=1,fill=true,stroke=true,shoreColor=[57,126,158]}) {
  const halfWidth=1.1*pixelRatio/2;
  for(let y=y0;y<=y1;y+=step)for(let x=x0;x<=x1;x+=step) {
    const index=y*width+x;
    if(!Number.isFinite(ocean[index]))continue; // Outside the globe, not dry land.
    const distance=Math.max(distanceAt(ocean,index,x,y,width,height,step),distanceAt(lakes,index,x,y,width,height,step));
    const coverage=clamp01(.5+distance);
    const outline=stroke?clamp01(halfWidth+.5-Math.abs(distance))*.8:0;
    const landAlpha=Math.round(255*(1-coverage));
    const offset=index*4,colorOffset=index*3;
    const waterAlpha=fill?coverage:0,landWeight=(1-waterAlpha)*(1-outline),waterWeight=waterAlpha*(1-outline);
    const r=pixels[offset]*landWeight+waterColors[colorOffset]*waterWeight+shoreColor[0]*outline;
    const g=pixels[offset+1]*landWeight+waterColors[colorOffset+1]*waterWeight+shoreColor[1]*outline;
    const b=pixels[offset+2]*landWeight+waterColors[colorOffset+2]*waterWeight+shoreColor[2]*outline;
    for(let dy=0;dy<step&&y+dy<height;dy++)for(let dx=0;dx<step&&x+dx<width;dx++) {
      const target=((y+dy)*width+x+dx)*4;
      pixels[target]=r;pixels[target+1]=g;pixels[target+2]=b;
      mask[target+3]=landAlpha;
    }
  }
}
