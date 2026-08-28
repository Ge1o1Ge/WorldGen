using System.Text.Json.Nodes;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class Institutions
{
    public static void Advance(WorldState world, ContentCatalog content)
    {
        if (world.Day == 0 || world.Day % 30 != 0) return;
        var recipes = content.Recipes.Recipes.ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; var population = world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            var coverage = city.Stocks["food"] / Math.Max(0.001, population * city.FoodPerPersonPerDay);
            foreach (var institution in city.Institutions)
            {
                var action = "maintain_capacity"; string? changed = null;
                if (coverage < city.LocalReserveDays * 0.7 || city.Shortage.Active)
                {
                    action = "secure_food"; city.LocalReserveDays = Math.Min(36, city.LocalReserveDays + 1);
                    var industry = city.Industries.Where(item => recipes[item.RecipeId].Outputs.ContainsKey("food"))
                        .OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (industry is not null) { industry.Capacity = Math.Min(industry.InitialCapacity * 1.6,
                        industry.Capacity + industry.InitialCapacity * 0.04 * institution.Competence); changed = industry.Id; }
                }
                else if (coverage > city.LocalReserveDays * 3)
                {
                    var industry = city.Industries.Where(item => recipes[item.RecipeId].Outputs.ContainsKey("food"))
                        .OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (industry is not null && industry.Capacity > industry.InitialCapacity * 0.65)
                    { action = "reduce_food_surplus"; industry.Capacity = Math.Max(industry.InitialCapacity * 0.65,
                        industry.Capacity - industry.InitialCapacity * 0.018 * institution.Competence); changed = industry.Id; }
                }
                else if (world.Day % 90 == 0 && coverage > city.LocalReserveDays * 1.5)
                {
                    var industry = city.Industries.Where(item => PriorityMatch(recipes[item.RecipeId], institution.Priorities))
                        .OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (industry is not null && industry.LastConstraintKey is null)
                    { action = "expand_specialty"; industry.Capacity = Math.Min(industry.InitialCapacity * 1.3,
                        industry.Capacity + industry.InitialCapacity * 0.006 * institution.Competence); changed = industry.Id; }
                }
                institution.Decisions++;
                if (world.Day % 90 == 0 || action != "maintain_capacity")
                    Journal.Record(world, "institution_decision", institution.Id,
                        city.Shortage.EventId is null ? [] : [city.Shortage.EventId],
                        new JsonObject { ["cityId"] = cityId, ["action"] = action, ["changedIndustryId"] = changed,
                            ["foodCoverageDays"] = SimulationMath.Round(coverage, 10), ["localReserveDays"] = city.LocalReserveDays });
            }
        }
    }

    private static bool PriorityMatch(RecipeDefinition recipe, IReadOnlyList<string> priorities) => priorities.Any(priority =>
        priority == recipe.Category || priority == "agriculture" && recipe.Category is "agriculture" or "pastoral" ||
        priority == "food_security" && recipe.Outputs.ContainsKey("food") || priority == "tools" && recipe.Outputs.ContainsKey("tools") ||
        priority == "fuel" && (recipe.Outputs.ContainsKey("firewood") || recipe.Outputs.ContainsKey("charcoal")) ||
        priority == "mining" && recipe.Category == "extraction");
}
