export function seasonalNeedFactor(world, seasonality) {
  if (!seasonality) return 1;
  const dayOfYear = world.day % world.calendar.daysPerYear;
  const wave = 0.5 + 0.5 * Math.cos(
    Math.PI * 2 * (dayOfYear - seasonality.peakDay) / world.calendar.daysPerYear
  );
  return seasonality.minimum + (1 - seasonality.minimum) * wave;
}

export function dailyHouseholdNeed(world, city, resource) {
  if (!resource.householdNeed) return 0;
  const population = world.spatial.nodes[city.spatialNodeId].aggregate.population;
  const perPerson = resource.id === "food"
    ? city.foodPerPersonPerDay
    : resource.householdNeed.perPersonPerDay;
  return population * perPerson * seasonalNeedFactor(world, resource.householdNeed.seasonality);
}
