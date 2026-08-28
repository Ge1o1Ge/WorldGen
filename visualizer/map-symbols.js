// Canvas and the SVG atlas use exactly the same editable path catalogue.
export function createSymbolRenderer(atlas) {
  const definitions = new Map(atlas.symbols.map(symbol => [symbol.id, symbol]));
  const paths = new Map();
  return function drawSymbol(context, id, x, y, size = 17, opacity = 1) {
    const symbol = definitions.get(id);
    if (!symbol) return;
    if (!paths.has(id)) paths.set(id, new Path2D(symbol.path));
    context.save();
    context.translate(x, y);
    context.scale(size / 24, size / 24);
    context.globalAlpha = opacity;
    context.lineWidth = 1.4 * 24 / size;
    context.lineJoin = "round";
    context.lineCap = "round";
    context.strokeStyle = context.fillStyle = atlas.palette[symbol.role];
    context.setLineDash(symbol.dash ?? []);
    if (symbol.fill) context.fill(paths.get(id)); else context.stroke(paths.get(id));
    context.restore();
  };
}

export function symbolSvg(atlas, symbol) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "-16 -16 32 32");
  svg.setAttribute("aria-hidden", "true");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", symbol.path);
  const color = atlas.palette[symbol.role];
  path.setAttribute("fill", symbol.fill ? color : "none");
  path.setAttribute("stroke", color);
  path.setAttribute("stroke-width", "1.4");
  path.setAttribute("stroke-linejoin", "round");
  path.setAttribute("stroke-linecap", "round");
  if (symbol.dash) path.setAttribute("stroke-dasharray", symbol.dash.join(" "));
  svg.append(path);
  return svg;
}
