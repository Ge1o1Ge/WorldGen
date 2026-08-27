import { recordEvent } from "./journal.js";
import { resourceTargetStock } from "./demand.js";

const EPSILON = 1e-9;

function quantize(value) {
  return Math.round(value * 1_000_000) / 1_000_000;
}

function incomingAmount(world, cityId, resourceId) {
  return world.shipments
    .filter((shipment) => shipment.to === cityId && shipment.resourceId === resourceId)
    .reduce((total, shipment) => total + shipment.amount, 0);
}

function buildAdjacency(routes, remainingCapacity) {
  const adjacency = new Map();
  for (const route of routes) {
    if ((remainingCapacity.get(route.id) ?? 0) <= EPSILON) continue;
    if (!adjacency.has(route.a)) adjacency.set(route.a, []);
    if (!adjacency.has(route.b)) adjacency.set(route.b, []);
    adjacency.get(route.a).push({ cityId: route.b, route });
    adjacency.get(route.b).push({ cityId: route.a, route });
  }
  for (const neighbors of adjacency.values()) {
    neighbors.sort((left, right) => left.route.id.localeCompare(right.route.id));
  }
  return adjacency;
}

function shortestPath(routes, from, to, remainingCapacity) {
  const adjacency = buildAdjacency(routes, remainingCapacity);
  const best = new Map([[from, { distance: 0, key: "", routeIds: [] }]]);
  const pending = new Set([from]);
  while (pending.size > 0) {
    const current = [...pending].sort((leftId, rightId) => {
      const left = best.get(leftId);
      const right = best.get(rightId);
      return left.distance - right.distance || left.key.localeCompare(right.key) || leftId.localeCompare(rightId);
    })[0];
    pending.delete(current);
    if (current === to) return best.get(current);
    const currentBest = best.get(current);
    for (const edge of adjacency.get(current) ?? []) {
      const candidate = {
        distance: currentBest.distance + edge.route.travelDays,
        key: `${currentBest.key}/${edge.route.id}`,
        routeIds: [...currentBest.routeIds, edge.route.id]
      };
      const known = best.get(edge.cityId);
      if (!known || candidate.distance < known.distance ||
          (candidate.distance === known.distance && candidate.key.localeCompare(known.key) < 0)) {
        best.set(edge.cityId, candidate);
        pending.add(edge.cityId);
      }
    }
  }
  return null;
}

export function deliverShipments(world, telemetry = null) {
  const arrived = world.shipments
    .filter((shipment) => shipment.arrivalDay <= world.day)
    .sort((left, right) => left.id.localeCompare(right.id));
  const arrivedIds = new Set(arrived.map(({ id }) => id));
  world.shipments = world.shipments.filter((shipment) => !arrivedIds.has(shipment.id));
  for (const shipment of arrived) {
    const city = world.cities[shipment.to];
    city.stocks[shipment.resourceId] = quantize(city.stocks[shipment.resourceId] + shipment.amount);
    recordEvent(world, {
      type: "shipment_arrived",
      subjectId: shipment.id,
      causeIds: [shipment.dispatchEventId],
      details: { from: shipment.from, to: shipment.to, resourceId: shipment.resourceId, amount: shipment.amount }
    });
    if (telemetry) telemetry.shipmentsArrived += 1;
  }
}

function outstandingIntent(world, cityId, resourceId, kind) {
  return world.tradeIntents
    .filter((intent) => intent.cityId === cityId && intent.resourceId === resourceId && intent.kind === kind)
    .reduce((sum, intent) => sum + intent.remaining, 0);
}

function createIntent(world, kind, city, resourceId, amount) {
  world.tradeIntents.push({
    id: `intent-${String(world.nextTradeIntentId).padStart(6, "0")}`,
    kind,
    cityId: city.id,
    resourceId,
    amount: quantize(amount),
    remaining: quantize(amount),
    createdDay: world.day,
    availableDay: world.day + 1,
    expiresDay: world.day + 12,
    limitPrice: kind === "demand"
      ? Math.round(city.markets[resourceId].price * 1.18 * 10_000) / 10_000
      : Math.round(city.markets[resourceId].price * 0.9 * 10_000) / 10_000
  });
  world.nextTradeIntentId += 1;
}

function publishLocalIntents(world, content) {
  if (world.day % 3 !== 0) return;
  const resourceIds = content.resources.resources.map((resource) => resource.id).sort();
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    for (const resourceId of resourceIds) {
      const target = resourceTargetStock(world, city, resourceId, content);
      const effective = city.stocks[resourceId] + incomingAmount(world, cityId, resourceId);
      const demand = target - effective - outstandingIntent(world, cityId, resourceId, "demand");
      if (target > EPSILON && demand > EPSILON) createIntent(world, "demand", city, resourceId, demand);
      const protectedStock = target * 1.15;
      const offer = city.stocks[resourceId] - protectedStock -
        outstandingIntent(world, cityId, resourceId, "offer");
      if (offer > EPSILON) createIntent(world, "offer", city, resourceId, offer);
    }
  }
}

function matchIntents(world, content, telemetry) {
  const recipeById = new Map(content.recipes.recipes.map((recipe) => [recipe.id, recipe]));
  const remainingCapacity = new Map(world.routes.map((route) => [route.id, route.dailyCapacity]));
  const demands = world.tradeIntents
    .filter((intent) => intent.kind === "demand" && intent.availableDay <= world.day && intent.remaining > EPSILON)
    .sort((left, right) => left.resourceId.localeCompare(right.resourceId) ||
      left.createdDay - right.createdDay || left.id.localeCompare(right.id));

  for (const demand of demands) {
    const offers = world.tradeIntents
      .filter((intent) => intent.kind === "offer" && intent.resourceId === demand.resourceId &&
        intent.cityId !== demand.cityId && intent.availableDay <= world.day && intent.remaining > EPSILON)
      .map((offer) => ({ offer, path: shortestPath(world.routes, offer.cityId, demand.cityId, remainingCapacity) }))
      .filter((candidate) => candidate.path && demand.limitPrice >=
        candidate.offer.limitPrice * (1 + candidate.path.distance * 0.015))
      .sort((left, right) => left.path.distance - right.path.distance ||
        left.path.key.localeCompare(right.path.key) || left.offer.id.localeCompare(right.offer.id));

    for (const { offer, path } of offers) {
      if (demand.remaining <= EPSILON) break;
      const source = world.cities[offer.cityId];
      const target = resourceTargetStock(world, source, demand.resourceId, content);
      const actualSurplus = Math.max(0, source.stocks[demand.resourceId] - target * 1.05);
      const pathCapacity = Math.min(...path.routeIds.map((routeId) => remainingCapacity.get(routeId)));
      const amount = quantize(Math.min(demand.remaining, offer.remaining, actualSurplus, pathCapacity));
      if (amount <= EPSILON) continue;
      source.stocks[demand.resourceId] = quantize(source.stocks[demand.resourceId] - amount);
      demand.remaining = quantize(demand.remaining - amount);
      offer.remaining = quantize(offer.remaining - amount);
      for (const routeId of path.routeIds) {
        remainingCapacity.set(routeId, quantize(remainingCapacity.get(routeId) - amount));
      }

      const shipmentId = `shipment-${String(world.nextShipmentId).padStart(6, "0")}`;
      world.nextShipmentId += 1;
      const dispatchEvent = recordEvent(world, {
        type: "shipment_dispatched",
        subjectId: shipmentId,
        causeIds: source.resourceSignals[demand.resourceId] ? [source.resourceSignals[demand.resourceId]] : [],
        details: {
          from: offer.cityId,
          to: demand.cityId,
          resourceId: demand.resourceId,
          amount,
          routeIds: path.routeIds,
          offerIntentId: offer.id,
          demandIntentId: demand.id,
          unitPrice: Math.round(((offer.limitPrice + demand.limitPrice) / 2) * 10_000) / 10_000,
          arrivalDay: world.day + path.distance
        }
      });
      world.shipments.push({
        id: shipmentId,
        from: offer.cityId,
        to: demand.cityId,
        resourceId: demand.resourceId,
        amount,
        routeIds: path.routeIds,
        departureDay: world.day,
        arrivalDay: world.day + path.distance,
        dispatchEventId: dispatchEvent.id
      });
      telemetry.shipmentsDispatched += 1;
    }
    if (demand.remaining > EPSILON && !demand.shortfallEventId) {
      const causeIds = Object.values(world.cities).flatMap((city) => city.industries
        .filter((industry) => Object.hasOwn(recipeById.get(industry.recipeId).outputs, demand.resourceId))
        .map((industry) => industry.constraintEventId)
        .filter(Boolean));
      const shortfall = recordEvent(world, {
        type: "trade_shortfall",
        subjectId: demand.id,
        causeIds,
        details: {
          cityId: demand.cityId,
          resourceId: demand.resourceId,
          missingAmount: demand.remaining
        }
      });
      demand.shortfallEventId = shortfall.id;
      world.cities[demand.cityId].resourceSignals[demand.resourceId] = shortfall.id;
    }
  }
  world.shipments.sort((left, right) => left.id.localeCompare(right.id));
}

export function planShipments(world, content, telemetry = { shipmentsDispatched: 0 }) {
  world.tradeIntents = world.tradeIntents.filter((intent) =>
    intent.remaining > EPSILON && intent.expiresDay > world.day
  );
  publishLocalIntents(world, content);
  matchIntents(world, content, telemetry);
  world.tradeIntents = world.tradeIntents.filter((intent) =>
    intent.remaining > EPSILON && intent.expiresDay > world.day
  );
  world.tradeIntents.sort((left, right) => left.id.localeCompare(right.id));
}
