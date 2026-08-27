import { dailyHouseholdNeed } from "./needs.js";
import { infrastructureMonthlyNeed } from "./infrastructure.js";

export function dailyResourceNeed(world, city, resourceId, content) {
  const resource = content.resources.resources.find((item) => item.id === resourceId);
  let dailyNeed = dailyHouseholdNeed(world, city, resource);
  const recipeById = new Map(content.recipes.recipes.map((recipe) => [recipe.id, recipe]));
  for (const industry of city.industries) {
    const recipe = recipeById.get(industry.recipeId);
    dailyNeed += Number(recipe.inputs[resourceId] ?? 0) * industry.capacity;
  }
  const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
  dailyNeed += infrastructureMonthlyNeed(city, population, resourceId) / 30;
  return dailyNeed;
}

export function resourceTargetStock(world, city, resourceId, content) {
  const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
  const monthlyInfrastructure = infrastructureMonthlyNeed(city, population, resourceId);
  return dailyResourceNeed(world, city, resourceId, content) * city.localReserveDays +
    monthlyInfrastructure * Math.max(0, 1.2 - city.localReserveDays / 30);
}
