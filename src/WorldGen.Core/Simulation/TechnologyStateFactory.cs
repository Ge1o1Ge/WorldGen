using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class TechnologyStateFactory
{
    private static readonly string[] Dimensions = ["knowledge", "competence", "capability", "adoption"];
    private static readonly double[] MilestoneThresholds = [0.25, 0.5, 0.75, 0.95];

    public static Dictionary<string, TechnologyState> Create(ContentCatalog content, CityDefinition city)
    {
        return content.Technologies.Technologies
            .OrderBy(technology => technology.Id, StringComparer.Ordinal)
            .ToDictionary(
                technology => technology.Id,
                technology =>
                {
                    var values = city.TechnologySeeds.TryGetValue(technology.Id, out var seed)
                        ? seed
                        : [0.03, 0.01, 0, 0];
                    return new TechnologyState
                    {
                        Knowledge = values[0],
                        Competence = values[1],
                        Capability = values[2],
                        Adoption = values[3],
                        Milestones = Dimensions.ToDictionary(
                            dimension => dimension,
                            dimension => MilestoneThresholds.Count(threshold =>
                                values[Array.IndexOf(Dimensions, dimension)] >= threshold),
                            StringComparer.Ordinal)
                    };
                },
                StringComparer.Ordinal);
    }
}
