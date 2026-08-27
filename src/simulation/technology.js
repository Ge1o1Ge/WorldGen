import { recordEvent } from "./journal.js";

const DIMENSIONS = ["knowledge", "competence", "capability", "adoption"];
const MILESTONES = [0.25, 0.5, 0.75, 0.95];

function clamp01(value) {
  return Math.max(0, Math.min(1, value));
}

function quantize(value) {
  return Math.round(clamp01(value) * 1_000_000) / 1_000_000;
}

export function createTechnologyState(content, cityDefinition) {
  return Object.fromEntries([...content.technologies.technologies]
    .sort((left, right) => left.id.localeCompare(right.id))
    .map((technology) => {
      const values = cityDefinition.technologySeeds[technology.id] ?? [0.03, 0.01, 0, 0];
      const state = Object.fromEntries(DIMENSIONS.map((dimension, index) => [dimension, values[index]]));
      state.milestones = Object.fromEntries(DIMENSIONS.map((dimension) => [
        dimension,
        MILESTONES.filter((threshold) => state[dimension] >= threshold).length
      ]));
      return [technology.id, state];
    }));
}

function requiredPredecessors(content, technologyId) {
  return content.technologies.relations
    .filter((relation) => relation.to === technologyId && relation.type === "required")
    .map((relation) => relation.from)
    .sort();
}

function supportingPredecessors(content, technologyId) {
  return content.technologies.relations
    .filter((relation) => relation.to === technologyId && ["helps", "supports", "scientific"].includes(relation.type))
    .map((relation) => relation.from)
    .sort();
}

function industriesUsingTechnology(city, content, technologyId) {
  const recipeById = new Map(content.recipes.recipes.map((recipe) => [recipe.id, recipe]));
  return city.industries.filter((industry) =>
    recipeById.get(industry.recipeId).requiredTechnologyIds.includes(technologyId)
  );
}

function institutionLearning(city, domain) {
  if (city.institutions.length === 0) return 0.1;
  return city.institutions.reduce((best, institution) => {
    const focused = institution.priorities.some((priority) =>
      priority === domain || (domain === "agriculture" && priority === "food_security") ||
      (domain === "metallurgy" && priority === "tools") ||
      (domain === "transport" && priority === "trade")
    );
    return Math.max(best, institution.learningRate * (focused ? 1 : 0.55));
  }, 0.1);
}

function capabilityTarget(city, content, technologyId) {
  const industries = industriesUsingTechnology(city, content, technologyId);
  if (industries.length === 0) return Math.min(0.35, city.technologyState[technologyId].competence * 0.55);
  const activeShare = industries.reduce((sum, industry) => sum + (industry.capacity > 0 ? 1 : 0), 0) /
    industries.length;
  return Math.min(0.92, 0.42 + activeShare * 0.42);
}

function checkMilestones(world, city, technology, dimension, causeIds = []) {
  const state = city.technologyState[technology.id];
  let reached = state.milestones[dimension];
  while (reached < MILESTONES.length && state[dimension] >= MILESTONES[reached]) {
    const threshold = MILESTONES[reached];
    recordEvent(world, {
      type: "technology_milestone",
      subjectId: technology.id,
      causeIds,
      details: { cityId: city.id, technologyId: technology.id, dimension, threshold }
    });
    reached += 1;
  }
  state.milestones[dimension] = reached;
}

function applyKnowledgeTransfers(world, content) {
  const arrived = world.knowledgeTransfers
    .filter((transfer) => transfer.arrivalDay <= world.day)
    .sort((left, right) => left.id.localeCompare(right.id));
  const arrivedIds = new Set(arrived.map((transfer) => transfer.id));
  world.knowledgeTransfers = world.knowledgeTransfers.filter((transfer) => !arrivedIds.has(transfer.id));
  const technologyById = new Map(content.technologies.technologies.map((technology) => [technology.id, technology]));

  for (const transfer of arrived) {
    const city = world.cities[transfer.to];
    const technology = technologyById.get(transfer.technologyId);
    const learning = institutionLearning(city, technology.domain);
    const accepted = transfer.amount * (0.62 + learning * 0.38);
    city.technologyState[technology.id].knowledge = quantize(
      city.technologyState[technology.id].knowledge + accepted
    );
    checkMilestones(world, city, technology, "knowledge", [transfer.causeEventId].filter(Boolean));
  }
}

function scheduleKnowledgeTransfers(world, content) {
  const technologyDefinitions = [...content.technologies.technologies]
    .sort((left, right) => left.id.localeCompare(right.id));
  for (const route of [...world.routes].sort((left, right) => left.id.localeCompare(right.id))) {
    for (const technology of technologyDefinitions) {
      const left = world.cities[route.a].technologyState[technology.id].knowledge;
      const right = world.cities[route.b].technologyState[technology.id].knowledge;
      if (Math.abs(left - right) < 0.04) continue;
      const from = left > right ? route.a : route.b;
      const to = left > right ? route.b : route.a;
      const difference = Math.abs(left - right);
      const amount = quantize(difference * technology.diffusion * 0.035);
      if (amount <= 0.00001) continue;
      world.knowledgeTransfers.push({
        id: `knowledge-${String(world.nextKnowledgeTransferId).padStart(6, "0")}`,
        technologyId: technology.id,
        from,
        to,
        amount,
        departureDay: world.day,
        arrivalDay: world.day + route.travelDays * 2,
        routeId: route.id,
        causeEventId: null
      });
      world.nextKnowledgeTransferId += 1;
    }
  }
  world.knowledgeTransfers.sort((left, right) => left.id.localeCompare(right.id));
}

export function advanceTechnology(world, content) {
  applyKnowledgeTransfers(world, content);
  if (world.day === 0 || world.day % 30 !== 0) return;

  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    for (const technology of [...content.technologies.technologies]
      .sort((left, right) => left.id.localeCompare(right.id))) {
      const state = city.technologyState[technology.id];
      const required = requiredPredecessors(content, technology.id);
      const prerequisiteFactor = required.length === 0
        ? 1
        : Math.min(...required.map((id) => city.technologyState[id].knowledge));
      const support = supportingPredecessors(content, technology.id);
      const supportFactor = support.length === 0
        ? 0
        : support.reduce((sum, id) => sum + city.technologyState[id].knowledge, 0) / support.length;
      const industries = industriesUsingTechnology(city, content, technology.id);
      const exposure = industries.length > 0 ? 1 : 0.28;
      const learning = institutionLearning(city, technology.domain);
      const knowledgeIncrement = (1 - state.knowledge) * 0.009 * (0.35 + learning) * exposure *
        (1 - technology.complexity * 0.55) * (0.18 + prerequisiteFactor * 0.82) * (1 + supportFactor * 0.18);
      state.knowledge = quantize(state.knowledge + knowledgeIncrement);

      const competenceTarget = Math.min(state.knowledge, 0.15 + learning * 0.85);
      state.competence = quantize(state.competence +
        Math.max(0, competenceTarget - state.competence) * (0.035 + learning * 0.045));

      const targetCapability = Math.min(state.competence, capabilityTarget(city, content, technology.id));
      state.capability = quantize(state.capability +
        (targetCapability - state.capability) * (targetCapability >= state.capability ? 0.055 : 0.025));

      const targetAdoption = Math.min(state.knowledge, state.competence, state.capability);
      state.adoption = quantize(state.adoption +
        (targetAdoption - state.adoption) * (industries.length > 0 ? 0.065 : 0.018));

      for (const dimension of DIMENSIONS) checkMilestones(world, city, technology, dimension);
    }
  }
  scheduleKnowledgeTransfers(world, content);
}

export function technologyEfficiency(city, recipe) {
  if (recipe.requiredTechnologyIds.length === 0) return 1;
  const states = recipe.requiredTechnologyIds.map((technologyId) => city.technologyState[technologyId]);
  const effectivePractice = Math.min(...states.map((state) =>
    Math.min(state.knowledge, state.competence, state.capability, state.adoption)
  ));
  return 0.22 + effectivePractice * 0.78;
}
