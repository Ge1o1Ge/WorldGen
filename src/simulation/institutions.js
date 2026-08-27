import { recordEvent } from "./journal.js";

function recipePriorityMatch(recipe, priorities) {
  return priorities.some((priority) => priority === recipe.category ||
    (priority === "agriculture" && ["agriculture", "pastoral"].includes(recipe.category)) ||
    (priority === "food_security" && Object.hasOwn(recipe.outputs, "food")) ||
    (priority === "tools" && Object.hasOwn(recipe.outputs, "tools")) ||
    (priority === "fuel" && ["firewood", "charcoal"].some((id) => Object.hasOwn(recipe.outputs, id))) ||
    (priority === "mining" && recipe.category === "extraction"));
}

export function advanceInstitutions(world, content) {
  if (world.day === 0 || world.day % 30 !== 0) return;
  const recipeById = new Map(content.recipes.recipes.map((recipe) => [recipe.id, recipe]));
  for (const cityId of Object.keys(world.cities).sort()) {
    const city = world.cities[cityId];
    const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
    const dailyFoodNeed = Math.max(0.001, population * city.foodPerPersonPerDay);
    const foodCoverageDays = city.stocks.food / dailyFoodNeed;
    for (const institution of city.institutions) {
      let action = "maintain_capacity";
      let changedIndustryId = null;
      if (foodCoverageDays < city.localReserveDays * 0.7 || city.shortage.active) {
        action = "secure_food";
        city.localReserveDays = Math.min(36, city.localReserveDays + 1);
        const industry = city.industries
          .filter((candidate) => Object.hasOwn(recipeById.get(candidate.recipeId).outputs, "food"))
          .sort((left, right) => left.id.localeCompare(right.id))[0];
        if (industry) {
          industry.capacity = Math.min(industry.initialCapacity * 1.6,
            industry.capacity + industry.initialCapacity * 0.04 * institution.competence);
          changedIndustryId = industry.id;
        }
      } else if (foodCoverageDays > city.localReserveDays * 3) {
        const industry = city.industries
          .filter((candidate) => Object.hasOwn(recipeById.get(candidate.recipeId).outputs, "food"))
          .sort((left, right) => left.id.localeCompare(right.id))[0];
        if (industry && industry.capacity > industry.initialCapacity * 0.65) {
          action = "reduce_food_surplus";
          industry.capacity = Math.max(industry.initialCapacity * 0.65,
            industry.capacity - industry.initialCapacity * 0.018 * institution.competence);
          changedIndustryId = industry.id;
        }
      } else if (world.day % 90 === 0 && foodCoverageDays > city.localReserveDays * 1.5) {
        const industry = city.industries
          .filter((candidate) => recipePriorityMatch(recipeById.get(candidate.recipeId), institution.priorities))
          .sort((left, right) => left.id.localeCompare(right.id))[0];
        if (industry && industry.lastConstraintKey === null) {
          action = "expand_specialty";
          industry.capacity = Math.min(industry.initialCapacity * 1.3,
            industry.capacity + industry.initialCapacity * 0.006 * institution.competence);
          changedIndustryId = industry.id;
        }
      }
      institution.decisions += 1;
      if (world.day % 90 === 0 || action !== "maintain_capacity") {
        recordEvent(world, {
          type: "institution_decision",
          subjectId: institution.id,
          causeIds: city.shortage.eventId ? [city.shortage.eventId] : [],
          details: {
            cityId,
            action,
            changedIndustryId,
            foodCoverageDays: Math.round(foodCoverageDays * 10) / 10,
            localReserveDays: city.localReserveDays
          }
        });
      }
    }
  }
}
