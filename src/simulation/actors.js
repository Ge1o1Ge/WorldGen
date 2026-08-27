import { recordEvent } from "./journal.js";
import { citySpatialNodeId, zoneId } from "./grid-spatial.js";

function actorFromDefinition(definition, spatial, provenance) {
  const territoryId = definition.territoryId ?? zoneId(definition.zone.x, definition.zone.y);
  const territory = spatial.territories[territoryId];
  return {
    id: definition.id,
    kind: "person",
    name: definition.name,
    role: definition.role,
    location: {
      territoryId,
      cityId: territory.assignedCityId,
      spatialNodeId: citySpatialNodeId(territory.assignedCityId)
    },
    importance: {
      score: definition.importance,
      reasons: [...definition.reasons]
    },
    representedInPopulation: true,
    provenance,
    knowledgeState: {}
  };
}

export function createInitialActors(content, spatial) {
  return Object.fromEntries(
    [...content.scenario.importantActors]
      .sort((left, right) => left.id.localeCompare(right.id))
      .map((definition) => [definition.id, actorFromDefinition(definition, spatial, {
        type: "scenario",
        causeEventId: null
      })])
  );
}

export function materializeSignificantActor(world, definition, causeEventId = null) {
  if (world.actors[definition.id]) throw new Error(`Актор '${definition.id}' уже существует`);
  const territoryId = definition.territoryId ?? zoneId(definition.zone.x, definition.zone.y);
  if (!world.spatial.territories[territoryId]) {
    throw new Error(`Неизвестная территория актора '${territoryId}'`);
  }

  const actor = actorFromDefinition(definition, world.spatial, {
    type: "promoted_from_population",
    causeEventId
  });
  world.actors[actor.id] = actor;
  const event = recordEvent(world, {
    type: "actor_became_significant",
    subjectId: actor.id,
    causeIds: causeEventId ? [causeEventId] : [],
    details: {
      territoryId: actor.location.territoryId,
      cityId: actor.location.cityId,
      role: actor.role,
      importance: actor.importance.score
    }
  });

  const cityNode = world.spatial.nodes[actor.location.spatialNodeId];
  if (cityNode.detail && !cityNode.detail.actorIds.includes(actor.id)) {
    cityNode.detail.actorIds.push(actor.id);
    cityNode.detail.actorIds.sort();
  }
  return { actor, event };
}
