export function recordEvent(world, event) {
  const entry = {
    id: `event-${String(world.nextEventId).padStart(6, "0")}`,
    day: world.day,
    type: event.type,
    subjectId: event.subjectId ?? null,
    causeIds: [...new Set(event.causeIds ?? [])].sort(),
    details: event.details ?? {}
  };
  world.nextEventId += 1;
  world.journal.push(entry);
  return entry;
}

export function traceCauses(world, eventId) {
  const byId = new Map(world.journal.map((event) => [event.id, event]));
  const result = [];
  const visited = new Set();

  function visit(id, depth) {
    if (visited.has(id)) return;
    const event = byId.get(id);
    if (!event) return;
    visited.add(id);
    result.push({ depth, event });
    for (const causeId of event.causeIds) visit(causeId, depth + 1);
  }

  visit(eventId, 0);
  return result;
}
