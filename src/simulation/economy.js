import { recordEvent } from "./journal.js";
import { technologyEfficiency } from "./technology.js";
import { dailyHouseholdNeed } from "./needs.js";

const EPSILON = 1e-9;

function quantize(value) {
  return Math.round(value * 1_000_000) / 1_000_000;
}

function seasonalityFactor(world, recipe) {
  if (!recipe.seasonality) return 1;
  const dayOfYear = world.day % world.calendar.daysPerYear;
  const wave = 0.5 + 0.5 * Math.cos(
    Math.PI * 2 * (dayOfYear - recipe.seasonality.peakDay) / world.calendar.daysPerYear
  );
  return recipe.seasonality.minimum + (1 - recipe.seasonality.minimum) * wave;
}

function siteFactor(world, industry, recipe) {
  if (!recipe.sitePotential) return 1;
  const territory = world.spatial.territories[industry.zoneId];
  const basePotential = territory.resourcePotential[recipe.sitePotential];
  let remainingRatio = 1;
  if (recipe.sitePotential === "arable" || recipe.sitePotential === "pasture") {
    remainingRatio = territory.fertility > 0
      ? Math.max(0.15, territory.naturalState.soilQuality / territory.fertility)
      : 0.15;
  } else if (recipe.sitePotential === "timber") {
    remainingRatio = territory.forestCover > 0
      ? Math.max(0.05, territory.naturalState.forestBiomass / territory.forestCover)
      : 0;
  } else if (recipe.sitePotential === "fish") {
    remainingRatio = basePotential > 0
      ? Math.max(0.08, territory.naturalState.fishStock / basePotential)
      : 0;
  } else if (["clay", "stone", "iron_ore"].includes(recipe.sitePotential)) {
    remainingRatio = territory.naturalState.deposits[recipe.sitePotential];
  }
  return 0.18 + basePotential * remainingRatio * 0.82;
}

function recoverNaturalSites(world) {
  const siteIds = new Set(Object.values(world.cities)
    .flatMap((city) => city.industries.map((industry) => industry.zoneId)));
  for (const territoryId of [...siteIds].sort()) {
    const territory = world.spatial.territories[territoryId];
    const natural = territory.naturalState;
    natural.soilQuality = quantize(natural.soilQuality +
      (territory.fertility - natural.soilQuality) * 0.00045);
    natural.forestBiomass = quantize(natural.forestBiomass +
      (territory.forestCover - natural.forestBiomass) * 0.0003);
    natural.fishStock = quantize(natural.fishStock +
      (territory.resourcePotential.fish - natural.fishStock) * 0.004);
  }
}

function applyEnvironmentalUse(city, industry, recipe, territory, batches) {
  if (!recipe.sitePotential || batches <= 0) return;
  const natural = territory.naturalState;
  const potential = recipe.sitePotential;
  if (potential === "arable") {
    const rotation = city.technologyState.crop_rotation?.adoption ?? 0;
    natural.soilQuality = quantize(Math.max(0.05,
      natural.soilQuality - batches * 0.000055 * (1 - rotation * 0.65)
    ));
  } else if (potential === "pasture") {
    natural.soilQuality = quantize(Math.max(0.05, natural.soilQuality - batches * 0.000025));
  } else if (potential === "timber") {
    natural.forestBiomass = quantize(Math.max(0,
      natural.forestBiomass - batches * 0.00014
    ));
  } else if (potential === "fish") {
    natural.fishStock = quantize(Math.max(0, natural.fishStock - batches * 0.00016));
  } else if (["clay", "stone", "iron_ore"].includes(potential)) {
    const depletion = { clay: 0.000025, stone: 0.000018, iron_ore: 0.00007 }[potential];
    natural.deposits[potential] = quantize(Math.max(0,
      natural.deposits[potential] - batches * depletion
    ));
  }
  natural.extractedBatches[potential] = quantize(
    (natural.extractedBatches[potential] ?? 0) + batches
  );
}

function decayStocks(world, content, telemetry) {
  const resourceById = new Map(content.resources.resources.map((resource) => [resource.id, resource]));
  for (const city of Object.values(world.cities)) {
    for (const resourceId of Object.keys(city.stocks).sort()) {
      const decay = resourceById.get(resourceId).decayPerDay;
      if (decay <= 0 || city.stocks[resourceId] <= 0) continue;
      const lost = quantize(city.stocks[resourceId] * decay);
      city.stocks[resourceId] = quantize(Math.max(0, city.stocks[resourceId] - lost));
      telemetry.decayedByResource[resourceId] = quantize(
        (telemetry.decayedByResource[resourceId] ?? 0) + lost
      );
    }
  }
}

function updateConstraint(world, industry, recipe, city, plannedBatches, actualBatches, constraints, causeIds) {
  const key = constraints.join("|");
  if (constraints.length === 0) {
    if (industry.lastConstraintKey !== null) {
      recordEvent(world, {
        type: "production_restored",
        subjectId: industry.id,
        causeIds: industry.constraintEventId ? [industry.constraintEventId] : [],
        details: { cityId: city.id, recipeId: recipe.id }
      });
    }
    industry.lastConstraintKey = null;
    industry.constraintEventId = null;
    return;
  }
  if (key === industry.lastConstraintKey) return;
  const event = recordEvent(world, {
    type: "production_constrained",
    subjectId: industry.id,
    causeIds,
    details: { cityId: city.id, recipeId: recipe.id, plannedBatches, actualBatches, constraints }
  });
  industry.lastConstraintKey = key;
  industry.constraintEventId = event.id;
  for (const resourceId of Object.keys(recipe.outputs).sort()) city.resourceSignals[resourceId] = event.id;
}

function produce(world, content, telemetry) {
  const recipeById = new Map(content.recipes.recipes.map((recipe) => [recipe.id, recipe]));
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
    const activeEffects = Object.entries(city.activeEffects).sort(([left], [right]) => left.localeCompare(right));
    const workforceMultiplier = activeEffects.reduce((value, [, effect]) => value * effect.multiplier, 1);
    let availableLabor = population * city.workerShare * workforceMultiplier;

    for (const industry of city.industries) {
      const recipe = recipeById.get(industry.recipeId);
      const plannedBatches = quantize(industry.capacity * seasonalityFactor(world, recipe) *
        siteFactor(world, industry, recipe) * technologyEfficiency(city, recipe));
      const laborLimit = availableLabor / recipe.laborPerBatch;
      let inputLimit = Infinity;
      for (const [resourceId, amount] of Object.entries(recipe.inputs).sort()) {
        if (amount > 0) inputLimit = Math.min(inputLimit, city.stocks[resourceId] / amount);
      }
      const batches = quantize(Math.max(0, Math.min(plannedBatches, laborLimit, inputLimit)));

      for (const [resourceId, amount] of Object.entries(recipe.inputs).sort()) {
        const consumed = quantize(amount * batches);
        city.stocks[resourceId] = quantize(Math.max(0, city.stocks[resourceId] - consumed));
        telemetry.industrialConsumptionByResource[resourceId] = quantize(
          (telemetry.industrialConsumptionByResource[resourceId] ?? 0) + consumed
        );
      }
      for (const [resourceId, amount] of Object.entries(recipe.outputs).sort()) {
        const produced = quantize(amount * batches);
        city.stocks[resourceId] = quantize(city.stocks[resourceId] + produced);
        telemetry.productionByResource[resourceId] = quantize(
          (telemetry.productionByResource[resourceId] ?? 0) + produced
        );
      }
      availableLabor = quantize(Math.max(0, availableLabor - recipe.laborPerBatch * batches));
      industry.totalBatches = quantize(industry.totalBatches + batches);
      applyEnvironmentalUse(city, industry, recipe, world.spatial.territories[industry.zoneId], batches);

      const constraints = [];
      const causeIds = [];
      if (laborLimit + EPSILON < plannedBatches) {
        constraints.push("labor");
        causeIds.push(...activeEffects.map(([, effect]) => effect.startEventId));
      }
      for (const [resourceId, amount] of Object.entries(recipe.inputs).sort()) {
        if (amount > 0 && city.stocks[resourceId] + amount * batches < amount * plannedBatches - EPSILON) {
          constraints.push(`input:${resourceId}`);
          if (city.resourceSignals[resourceId]) causeIds.push(city.resourceSignals[resourceId]);
        }
      }
      updateConstraint(world, industry, recipe, city, plannedBatches, batches, constraints, causeIds);
    }
  }
}

function consumeFood(world, telemetry) {
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
    const needed = quantize(population * city.foodPerPersonPerDay);
    const consumed = quantize(Math.min(needed, city.stocks.food));
    const missing = quantize(needed - consumed);
    city.stocks.food = quantize(Math.max(0, city.stocks.food - consumed));
    telemetry.householdFoodConsumed = quantize(telemetry.householdFoodConsumed + consumed);
    telemetry.householdFoodMissing = quantize(telemetry.householdFoodMissing + missing);
    telemetry.householdConsumptionByResource.food = quantize(
      (telemetry.householdConsumptionByResource.food ?? 0) + consumed
    );
    telemetry.householdMissingByResource.food = quantize(
      (telemetry.householdMissingByResource.food ?? 0) + missing
    );

    if (missing > EPSILON) {
      city.shortage.days += 1;
      city.shortage.episodeDays += 1;
      city.shortage.missingStreak += 1;
      city.shortage.satisfiedStreak = 0;
      city.shortage.totalFoodMissing = quantize(city.shortage.totalFoodMissing + missing);
      if (!city.shortage.active && city.shortage.missingStreak >= 2) {
        const event = recordEvent(world, {
          type: "food_shortage_started",
          subjectId: cityId,
          causeIds: city.resourceSignals.food ? [city.resourceSignals.food] : [],
          details: { cityId, needed, available: consumed, missing }
        });
        city.shortage.active = true;
        city.shortage.eventId = event.id;
      }
    } else {
      city.shortage.missingStreak = 0;
      if (!city.shortage.active) city.shortage.episodeDays = 0;
      if (city.shortage.active) {
        city.shortage.satisfiedStreak += 1;
        if (city.shortage.satisfiedStreak >= 3) {
          recordEvent(world, {
            type: "food_shortage_ended",
            subjectId: cityId,
            causeIds: [city.shortage.eventId],
            details: { cityId, durationDays: city.shortage.episodeDays }
          });
          city.shortage.active = false;
          city.shortage.episodeDays = 0;
          city.shortage.satisfiedStreak = 0;
          city.shortage.eventId = null;
        }
      }
    }
  }
}

function consumeOtherHouseholdNeeds(world, content, telemetry) {
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    for (const resource of content.resources.resources.filter((item) =>
      item.householdNeed && item.id !== "food"
    ).sort((left, right) => left.id.localeCompare(right.id))) {
      const state = city.needs[resource.id];
      const needed = quantize(dailyHouseholdNeed(world, city, resource));
      const consumed = quantize(Math.min(needed, city.stocks[resource.id]));
      const missing = quantize(needed - consumed);
      city.stocks[resource.id] = quantize(Math.max(0, city.stocks[resource.id] - consumed));
      telemetry.householdConsumptionByResource[resource.id] = quantize(
        (telemetry.householdConsumptionByResource[resource.id] ?? 0) + consumed
      );
      telemetry.householdMissingByResource[resource.id] = quantize(
        (telemetry.householdMissingByResource[resource.id] ?? 0) + missing
      );
      if (missing > EPSILON) {
        state.days += 1;
        state.episodeDays += 1;
        state.missingStreak += 1;
        state.satisfiedStreak = 0;
        state.totalMissing = quantize(state.totalMissing + missing);
        if (!state.active && state.missingStreak >= 3) {
          const event = recordEvent(world, {
            type: "resource_shortage_started",
            subjectId: `${cityId}:${resource.id}`,
            causeIds: city.resourceSignals[resource.id] ? [city.resourceSignals[resource.id]] : [],
            details: { cityId, resourceId: resource.id, needed, available: consumed, missing }
          });
          state.active = true;
          state.eventId = event.id;
        }
      } else {
        state.missingStreak = 0;
        if (!state.active) state.episodeDays = 0;
        if (state.active) {
          state.satisfiedStreak += 1;
          if (state.satisfiedStreak >= 3) {
            recordEvent(world, {
              type: "resource_shortage_ended",
              subjectId: `${cityId}:${resource.id}`,
              causeIds: state.eventId ? [state.eventId] : [],
              details: { cityId, resourceId: resource.id, durationDays: state.episodeDays }
            });
            state.active = false;
            state.episodeDays = 0;
            state.satisfiedStreak = 0;
            state.eventId = null;
          }
        }
      }
    }
  }
}

export function runEconomyDay(world, content) {
  const telemetry = {
    day: world.day,
    productionByResource: {},
    industrialConsumptionByResource: {},
    decayedByResource: {},
    householdFoodConsumed: 0,
    householdFoodMissing: 0,
    householdConsumptionByResource: {},
    householdMissingByResource: {},
    infrastructureConsumptionByResource: {},
    shipmentsDispatched: 0,
    shipmentsArrived: 0
  };
  decayStocks(world, content, telemetry);
  recoverNaturalSites(world);
  produce(world, content, telemetry);
  consumeFood(world, telemetry);
  consumeOtherHouseholdNeeds(world, content, telemetry);
  return telemetry;
}
