export function primitiveLines(city) {
  const p = city.settlement?.primitive;
  if (!p) return [];
  const lines = [];
  if (p.weather) {
    const w = p.weather;
    lines.push(`Погода за последний день: ${w.temperatureC.toFixed(1)} °C · осадки ${w.rainMm.toFixed(1)} мм · влажность почвы ${Math.round(w.soilWater * 100)}% · снег ${w.snow.toFixed(0)} мм водного эквивалента.`);
  }
  const daily = city.population > 0 ? city.population * .001 : 0;
  lines.push(`Сезонный календарь: целевой запас хранимой пищи ${(p.storedFoodTarget * 1000).toFixed(0)} кг; запасено ${((city.stocks.winter_food ?? 0) * 1000).toFixed(0)} кг${daily ? ` (≈${((city.stocks.winter_food ?? 0) / daily).toFixed(1)} дн.)` : ""}.`);
  lines.push(`Сушка/копчение за день: ${(p.preservedToday * 1000).toFixed(1)} кг · взято из зимних запасов ${(p.releasedToday * 1000).toFixed(1)} кг.`);
  lines.push(`Снаряжение: ${(city.stocks.stone_kit ?? 0).toFixed(1)} каменных комплектов · ${(city.stocks.primitive_bow ?? 0).toFixed(1)} луков со стрелами · ${(city.stocks.garments ?? 0).toFixed(1)} комплектов одежды.`);
  if (p.herdBiomass > 0) lines.push(`Приручённые животные: ${(p.herdBiomass * 1000).toFixed(0)} кг условной биомассы · уход ${p.herdCareHours.toFixed(1)} чел·ч · корм ${(p.herdFeedToday * 1000).toFixed(1)} кг.`);
  if (p.representative) lines.push(`Представитель совета: ${p.representative}. Часть голосов делегируется из существующего бюджета.`);
  return lines;
}

export function renderPrimitivePanel(city) {
  const panel = document.createElement("details"); panel.dataset.panelKey = "primitive";
  const title = document.createElement("summary"); title.textContent = "Сезоны, зимние запасы и снаряжение"; panel.append(title);
  for (const line of primitiveLines(city)) { const p = document.createElement("p"); p.textContent = line; panel.append(p); }
  return panel;
}
