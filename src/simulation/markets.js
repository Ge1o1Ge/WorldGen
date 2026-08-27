import { dailyResourceNeed, resourceTargetStock } from "./demand.js";
import { recordEvent } from "./journal.js";

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function incomingAmount(world, cityId, resourceId) {
  return world.shipments
    .filter((shipment) => shipment.to === cityId && shipment.resourceId === resourceId)
    .reduce((sum, shipment) => sum + shipment.amount, 0);
}

export function advanceMarkets(world, content) {
  if (world.day % 7 !== 0) return;
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    for (const resource of [...content.resources.resources].sort((a, b) => a.id.localeCompare(b.id))) {
      const market = city.markets[resource.id];
      const target = resourceTargetStock(world, city, resource.id, content);
      const effectiveStock = city.stocks[resource.id] + incomingAmount(world, cityId, resource.id);
      const dailyNeed = dailyResourceNeed(world, city, resource.id, content);
      const scarcity = target > 0
        ? clamp((target + 1) / (effectiveStock + 1), 0.08, 12)
        : clamp(1 / (1 + effectiveStock / 250), 0.35, 1);
      const rawPrice = resource.baseValue * clamp(Math.pow(scarcity, 0.62), 0.3, 4.5);
      const previousPrice = market.price;
      market.price = Math.round((previousPrice * 0.72 + rawPrice * 0.28) * 10_000) / 10_000;
      market.targetStock = Math.round(target * 1000) / 1000;
      market.coverageDays = dailyNeed > 0
        ? Math.round(effectiveStock / dailyNeed * 10) / 10
        : null;
      market.availability = target > 0 ? clamp(effectiveStock / target, 0, 2) : 1;

      const wasShock = market.shockActive;
      market.shockActive = market.price >= resource.baseValue * 2.5;
      if (market.shockActive && !wasShock) {
        const event = recordEvent(world, {
          type: "price_shock_started",
          subjectId: `${cityId}:${resource.id}`,
          causeIds: city.resourceSignals[resource.id] ? [city.resourceSignals[resource.id]] : [],
          details: { cityId, resourceId: resource.id, price: market.price, baseValue: resource.baseValue }
        });
        market.shockEventId = event.id;
      } else if (!market.shockActive && wasShock) {
        recordEvent(world, {
          type: "price_shock_ended",
          subjectId: `${cityId}:${resource.id}`,
          causeIds: market.shockEventId ? [market.shockEventId] : [],
          details: { cityId, resourceId: resource.id, price: market.price }
        });
        market.shockEventId = null;
      }
    }
  }
}
