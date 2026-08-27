import { activateCityDetail, activateTerritoryDetail, locateEventTerritory } from "./grid-spatial.js";
import { recordEvent } from "./journal.js";

export function advanceEndogenousEvents(world) {
  if (world.day === 0 || world.day % 30 !== 0) return;
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    if (Object.keys(city.activeEffects).length > 0) continue;
    const node = world.spatial.nodes[city.spatialNodeId];
    const wetlandShare = (node.aggregate.biomeShares.wetland ?? 0) +
      (node.aggregate.biomeShares.floodplain ?? 0) * 0.35;
    const density = node.aggregate.population / Math.max(1, node.childTerritoryIds.length);
    const traffic = world.journal.filter((event) =>
      event.day >= world.day - 30 && event.type === "shipment_arrived" && event.details.to === cityId
    ).length;
    const risk = 0.0008 + wetlandShare * 0.003 + Math.min(0.0015, density / 25_000) +
      (1 - city.demography.health) * 0.006 + Math.min(0.001, traffic * 0.00005);
    if (world.randomStreams.events.next() >= risk) continue;

    const durationDays = 24 + Math.floor(world.randomStreams.events.next() * 43);
    const multiplier = 0.58 + world.randomStreams.events.next() * 0.2;
    const territoryId = locateEventTerritory(world, cityId, world.randomStreams.events);
    const recentArrivalCauses = world.journal
      .filter((event) => event.day >= world.day - 30 && event.type === "shipment_arrived" && event.details.to === cityId)
      .slice(-3)
      .map((event) => event.id);
    const effectId = `endemic-fever:${cityId}:${world.day}`;
    const startEvent = recordEvent(world, {
      type: "crisis_started",
      subjectId: effectId,
      causeIds: recentArrivalCauses,
      details: {
        cityId,
        territoryId,
        label: `Лихорадка в поселении «${city.name}»`,
        multiplier,
        durationDays,
        endogenous: true,
        emergence: { wetlandShare, density, health: city.demography.health, traffic, risk }
      }
    });
    city.activeEffects[effectId] = {
      multiplier,
      endDay: world.day + durationDays,
      startEventId: startEvent.id,
      territoryId,
      label: startEvent.details.label,
      endogenous: true
    };
    activateCityDetail(world, cityId, {
      causeEventId: startEvent.id,
      keepActiveDays: durationDays + world.lodPolicy.crisisCooldownDays
    });
    activateTerritoryDetail(world, territoryId, {
      causeEventId: startEvent.id,
      keepActiveDays: durationDays + world.lodPolicy.crisisCooldownDays
    });
  }
}
