import { recordEvent } from "./journal.js";

const REPORTABLE_TYPES = new Set([
  "crisis_started",
  "food_shortage_started",
  "resource_shortage_started",
  "technology_milestone",
  "migration_flow",
  "price_shock_started"
]);

function shortestTravelDays(routes, from, to) {
  const best = new Map([[from, { days: 0, key: "" }]]);
  const pending = new Set([from]);
  while (pending.size > 0) {
    const current = [...pending].sort((leftId, rightId) => {
      const left = best.get(leftId);
      const right = best.get(rightId);
      return left.days - right.days || left.key.localeCompare(right.key) || leftId.localeCompare(rightId);
    })[0];
    pending.delete(current);
    if (current === to) return best.get(current).days;
    const neighbors = routes
      .filter((route) => route.a === current || route.b === current)
      .map((route) => ({ cityId: route.a === current ? route.b : route.a, route }))
      .sort((left, right) => left.route.id.localeCompare(right.route.id));
    for (const { cityId, route } of neighbors) {
      const candidate = { days: best.get(current).days + route.travelDays, key: `${best.get(current).key}/${route.id}` };
      const known = best.get(cityId);
      if (!known || candidate.days < known.days ||
          (candidate.days === known.days && candidate.key.localeCompare(known.key) < 0)) {
        best.set(cityId, candidate);
        pending.add(cityId);
      }
    }
  }
  return null;
}

function receiveReports(world) {
  const arrived = world.information.reports
    .filter((report) => report.arrivalDay <= world.day)
    .sort((left, right) => left.id.localeCompare(right.id));
  const arrivedIds = new Set(arrived.map((report) => report.id));
  world.information.reports = world.information.reports.filter((report) => !arrivedIds.has(report.id));
  for (const report of arrived) {
    world.cities[report.to].knowledgeState.observations[report.eventId] = {
      eventId: report.eventId,
      sourceCityId: report.sourceCityId,
      receivedDay: world.day,
      confidence: report.confidence,
      channel: report.channel,
      reportId: report.id
    };
    if (["crisis_started", "technology_milestone"].includes(report.eventType)) {
      recordEvent(world, {
        type: "information_received",
        subjectId: report.to,
        causeIds: [report.eventId],
        details: {
          cityId: report.to,
          sourceCityId: report.sourceCityId,
          reportedEventId: report.eventId,
          reportedEventType: report.eventType,
          delayDays: world.day - report.eventDay,
          confidence: report.confidence
        }
      });
    }
  }
}

function scheduleReports(world) {
  const newEvents = world.journal.slice(world.information.lastJournalIndex);
  for (const event of newEvents) {
    if (!REPORTABLE_TYPES.has(event.type)) continue;
    const sourceCityId = event.details.cityId ?? event.details.from ?? null;
    if (!sourceCityId || !world.cities[sourceCityId]) continue;
    world.cities[sourceCityId].knowledgeState.observations[event.id] = {
      eventId: event.id,
      sourceCityId,
      receivedDay: world.day,
      confidence: 1,
      channel: "direct",
      reportId: null
    };
    for (const to of Object.keys(world.cities).sort()) {
      if (to === sourceCityId) continue;
      const travelDays = shortestTravelDays(world.routes, sourceCityId, to);
      if (travelDays === null) continue;
      world.information.reports.push({
        id: `report-${String(world.information.nextReportId).padStart(6, "0")}`,
        eventId: event.id,
        eventType: event.type,
        eventDay: event.day,
        sourceCityId,
        to,
        departureDay: world.day,
        arrivalDay: world.day + travelDays * 2,
        confidence: Math.max(0.55, Math.round((0.96 - travelDays * 0.025) * 1000) / 1000),
        channel: "courier"
      });
      world.information.nextReportId += 1;
    }
  }
  world.information.lastJournalIndex = world.journal.length;
  world.information.reports.sort((left, right) => left.id.localeCompare(right.id));
}

export function advanceInformation(world) {
  receiveReports(world);
  scheduleReports(world);
}
