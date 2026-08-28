import { symbolSvg } from "./map-symbols.js";
try {
  const response = await fetch("/assets/topographic-symbols.json");
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const atlas = await response.json();
  for (const symbol of atlas.symbols) {
    const card = document.createElement("div");
    card.className = "atlas-card";
    const title = document.createElement("div");
    title.textContent = symbol.label;
    const id = document.createElement("small");
    id.textContent = symbol.id;
    title.append(id);
    card.append(symbolSvg(atlas, symbol), title);
    document.getElementById("atlas-grid").append(card);
  }
} catch (error) { document.getElementById("atlas-grid").textContent = `Не удалось загрузить обозначения: ${error.message}`; }
