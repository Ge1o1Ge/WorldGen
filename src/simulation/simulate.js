import { advanceDemography } from "./demography.js";
import { runEconomyDay } from "./economy.js";
import { advanceEndogenousEvents } from "./endogenous-events.js";
import { activateCityDetail, activateTerritoryDetail, collapseExpiredSpatialNodes, locateEventTerritory } from "./grid-spatial.js";
import { advanceInstitutions } from "./institutions.js";
import { advanceInformation } from "./information.js";
import { advanceInfrastructure } from "./infrastructure.js";
import { recordEvent } from "./journal.js";
import { deliverShipments, planShipments } from "./logistics.js";
import { advanceMarkets } from "./markets.js";
import { advanceTechnology } from "./technology.js";

function beginAndEndScheduledEvents(world) {
  for (const city of Object.values(world.cities)) {
    for (const [effectId, effect] of Object.entries(city.activeEffects).sort()) {
      if (effect.endDay !== world.day) continue;
      recordEvent(world, {
        type: "crisis_ended",
        subjectId: effectId,
        causeIds: [effect.startEventId],
        details: { cityId: city.id, label: effect.label, endogenous: effect.endogenous ?? false }
      });
      delete city.activeEffects[effectId];
    }
  }

  for (const scheduled of world.scheduledEvents) {
    if (scheduled.startDay !== world.day) continue;
    const territoryId = locateEventTerritory(world, scheduled.cityId, world.randomStreams.events);
    const startEvent = recordEvent(world, {
      type: "crisis_started",
      subjectId: scheduled.id,
      details: {
        cityId: scheduled.cityId,
        territoryId,
        label: scheduled.label,
        multiplier: scheduled.multiplier,
        durationDays: scheduled.durationDays,
        endogenous: false
      }
    });
    world.cities[scheduled.cityId].activeEffects[scheduled.id] = {
      multiplier: scheduled.multiplier,
      endDay: scheduled.startDay + scheduled.durationDays,
      startEventId: startEvent.id,
      territoryId,
      label: scheduled.label,
      endogenous: false
    };
    activateCityDetail(world, scheduled.cityId, {
      causeEventId: startEvent.id,
      keepActiveDays: scheduled.durationDays + world.lodPolicy.crisisCooldownDays
    });
    activateTerritoryDetail(world, territoryId, {
      causeEventId: startEvent.id,
      keepActiveDays: scheduled.durationDays + world.lodPolicy.crisisCooldownDays
    });
  }
}

function maintainShortageDetail(world) {
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    if (!city.shortage.active) continue;
    activateCityDetail(world, cityId, {
      causeEventId: city.shortage.eventId,
      keepActiveDays: Math.max(1, world.lodPolicy.shortageCooldownDays)
    });
    activateTerritoryDetail(world, world.spatial.nodes[city.spatialNodeId].anchorTerritoryId, {
      causeEventId: city.shortage.eventId,
      keepActiveDays: Math.max(1, world.lodPolicy.shortageCooldownDays)
    });
  }
}

export function stepWorld(world, content) {
  collapseExpiredSpatialNodes(world);
  beginAndEndScheduledEvents(world);
  advanceEndogenousEvents(world);
  advanceTechnology(world, content);
  advanceInstitutions(world, content);

  const transportTelemetry = { shipmentsArrived: 0, shipmentsDispatched: 0 };
  deliverShipments(world, transportTelemetry);
  const telemetry = runEconomyDay(world, content);
  advanceInfrastructure(world, telemetry);
  telemetry.shipmentsArrived = transportTelemetry.shipmentsArrived;
  advanceMarkets(world, content);
  planShipments(world, content, telemetry);
  maintainShortageDetail(world);
  advanceDemography(world);
  advanceInformation(world);

  world.telemetry.daily.push(telemetry);
  if (world.telemetry.daily.length > 730) world.telemetry.daily.shift();
  world.day += 1;
  return world;
}

export function simulateDays(world, content, days) {
  if (!Number.isInteger(days) || days < 0) {
    throw new Error("Количество дней симуляции должно быть неотрицательным целым числом");
  }
  for (let index = 0; index < days; index += 1) stepWorld(world, content);
  return world;
}
