using System.Text.Json.Nodes;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class Technology
{
    private static readonly string[] Dimensions = ["knowledge", "competence", "capability", "adoption"];
    private static readonly double[] Thresholds = [0.25, 0.5, 0.75, 0.95];

    public static void Advance(WorldState world, ContentCatalog content)
    {
        ApplyTransfers(world, content);
        if (world.Day == 0 || world.Day % 30 != 0) return;
        foreach (var cityId in world.Cities.Keys.Order(StringComparer.Ordinal))
        foreach (var definition in content.Technologies.Technologies.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var city = world.Cities[cityId]; var state = city.TechnologyState[definition.Id];
            var required = content.Technologies.Relations.Where(item => item.To == definition.Id && item.Type == "required")
                .Select(item => item.From).Order(StringComparer.Ordinal).ToArray();
            var prerequisite = required.Length == 0 ? 1 : required.Min(id => city.TechnologyState[id].Knowledge);
            var supporting = content.Technologies.Relations.Where(item => item.To == definition.Id && item.Type is "helps" or "supports" or "scientific")
                .Select(item => item.From).Order(StringComparer.Ordinal).ToArray();
            var support = supporting.Length == 0 ? 0 : supporting.Average(id => city.TechnologyState[id].Knowledge);
            var industries = IndustriesUsing(city, content, definition.Id);
            var exposure = industries.Count > 0 ? 1 : 0.28;
            var learning = InstitutionLearning(city, definition.Domain);
            var increment = (1 - state.Knowledge) * 0.009 * (0.35 + learning) * exposure *
                (1 - definition.Complexity * 0.55) * (0.18 + prerequisite * 0.82) * (1 + support * 0.18);
            state.Knowledge = SimulationMath.Quantize(state.Knowledge + increment);
            var competenceTarget = Math.Min(state.Knowledge, 0.15 + learning * 0.85);
            state.Competence = SimulationMath.Quantize(state.Competence + Math.Max(0, competenceTarget - state.Competence) * (0.035 + learning * 0.045));
            var capabilityTarget = Math.Min(state.Competence, CapabilityTarget(city, content, definition.Id));
            state.Capability = SimulationMath.Quantize(state.Capability + (capabilityTarget - state.Capability) * (capabilityTarget >= state.Capability ? 0.055 : 0.025));
            var adoptionTarget = Math.Min(Math.Min(state.Knowledge, state.Competence), state.Capability);
            state.Adoption = SimulationMath.Quantize(state.Adoption + (adoptionTarget - state.Adoption) * (industries.Count > 0 ? 0.065 : 0.018));
            foreach (var dimension in Dimensions) CheckMilestones(world, city, definition, dimension);
        }
        ScheduleTransfers(world, content);
    }

    private static void ApplyTransfers(WorldState world, ContentCatalog content)
    {
        var arrived = world.KnowledgeTransfers.Where(item => item.ArrivalDay <= world.Day).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var ids = arrived.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        world.KnowledgeTransfers = world.KnowledgeTransfers.Where(item => !ids.Contains(item.Id)).ToList();
        var definitions = content.Technologies.Technologies.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var transfer in arrived)
        {
            var city = world.Cities[transfer.To]; var definition = definitions[transfer.TechnologyId];
            var accepted = transfer.Amount * (0.62 + InstitutionLearning(city, definition.Domain) * 0.38);
            city.TechnologyState[definition.Id].Knowledge = SimulationMath.Quantize(city.TechnologyState[definition.Id].Knowledge + accepted);
            CheckMilestones(world, city, definition, "knowledge", transfer.CauseEventId is null ? [] : [transfer.CauseEventId]);
        }
    }

    private static void ScheduleTransfers(WorldState world, ContentCatalog content)
    {
        foreach (var route in world.Routes.OrderBy(item => item.Id, StringComparer.Ordinal))
        foreach (var definition in content.Technologies.Technologies.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var left = world.Cities[route.A].TechnologyState[definition.Id].Knowledge;
            var right = world.Cities[route.B].TechnologyState[definition.Id].Knowledge;
            if (Math.Abs(left - right) < 0.04) continue;
            var from = left > right ? route.A : route.B; var to = left > right ? route.B : route.A;
            var amount = SimulationMath.Quantize(Math.Abs(left - right) * definition.Diffusion * 0.035);
            if (amount <= 0.00001) continue;
            world.KnowledgeTransfers.Add(new KnowledgeTransferState($"knowledge-{world.NextKnowledgeTransferId:000000}",
                definition.Id, from, to, amount, world.Day, world.Day + route.TravelDays * 2, route.Id, null));
            world.NextKnowledgeTransferId++;
        }
        world.KnowledgeTransfers = world.KnowledgeTransfers.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
    }

    private static List<IndustryState> IndustriesUsing(CityState city, ContentCatalog content, string technologyId)
    {
        var recipes = content.Recipes.Recipes.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return city.Industries.Where(industry => recipes[industry.RecipeId].RequiredTechnologyIds.Contains(technologyId, StringComparer.Ordinal)).ToList();
    }

    private static double InstitutionLearning(CityState city, string domain)
    {
        if (city.Institutions.Count == 0) return 0.1;
        return city.Institutions.Aggregate(0.1, (best, institution) =>
        {
            var focused = institution.Priorities.Any(priority => priority == domain || domain == "agriculture" && priority == "food_security" ||
                domain == "metallurgy" && priority == "tools" || domain == "transport" && priority == "trade");
            return Math.Max(best, institution.LearningRate * (focused ? 1 : 0.55));
        });
    }

    private static double CapabilityTarget(CityState city, ContentCatalog content, string technologyId)
    {
        var industries = IndustriesUsing(city, content, technologyId);
        if (industries.Count == 0) return Math.Min(0.35, city.TechnologyState[technologyId].Competence * 0.55);
        var activeShare = industries.Count(item => item.Capacity > 0) / (double)industries.Count;
        return Math.Min(0.92, 0.42 + activeShare * 0.42);
    }

    private static void CheckMilestones(WorldState world, CityState city, TechnologyDefinition definition, string dimension,
        IEnumerable<string?>? causeIds = null)
    {
        var state = city.TechnologyState[definition.Id]; var value = dimension switch
        { "knowledge" => state.Knowledge, "competence" => state.Competence, "capability" => state.Capability, _ => state.Adoption };
        var reached = state.Milestones[dimension];
        while (reached < Thresholds.Length && value >= Thresholds[reached])
        {
            var threshold = Thresholds[reached];
            Journal.Record(world, "technology_milestone", definition.Id, causeIds,
                new JsonObject { ["cityId"] = city.Id, ["technologyId"] = definition.Id, ["dimension"] = dimension, ["threshold"] = threshold });
            reached++;
        }
        state.Milestones[dimension] = reached;
    }
}
