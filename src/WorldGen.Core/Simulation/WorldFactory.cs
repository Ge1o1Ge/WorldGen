using WorldGen.Core.Content;
using WorldGen.Core.Determinism;
using WorldGen.Core.Spatial;

namespace WorldGen.Core.Simulation;

public static class WorldFactory
{
    private static readonly string[] RandomStreamNames = ["economy", "events", "technology", "institutions"];

    public static WorldState Create(ContentCatalog content)
        => Create(content, SpatialGenerator.Build(content), zone => SpatialGenerator.ZoneId(zone.X, zone.Y));

    public static WorldState Create(ContentCatalog content, SpatialHierarchy spatial, Func<GridCoordinate, string> zoneId)
    {
        ArgumentNullException.ThrowIfNull(content);
        var cities = CreateCities(content, zoneId);

        foreach (var city in cities.Values)
        {
            foreach (var industry in city.Industries)
            {
                if (!spatial.Territories.TryGetValue(industry.ZoneId, out var territory) || territory.AssignedCityId != city.Id)
                {
                    throw new InvalidOperationException(
                        $"Площадка предприятия '{industry.Id}' не принадлежит городу '{city.Id}'");
                }
            }
        }

        return new WorldState
        {
            ScenarioId = content.Scenario.Id,
            Seed = content.Scenario.Seed,
            ContentFingerprint = content.Fingerprint,
            ContentSchemaVersions = new ContentSchemaVersions(
                content.Resources.SchemaVersion,
                content.Recipes.SchemaVersion,
                content.Technologies.SchemaVersion,
                content.Map.SchemaVersion,
                content.Scenario.SchemaVersion),
            Day = 0,
            Calendar = content.Scenario.Calendar,
            ReserveDays = content.Scenario.ReserveDays,
            DemographyPolicy = content.Scenario.Demography,
            LodPolicy = content.Scenario.LodPolicy,
            Spatial = spatial,
            Actors = CreateActors(content, spatial, zoneId),
            Cities = cities,
            Routes = content.Scenario.Routes
                .OrderBy(route => route.Id, StringComparer.Ordinal)
                .Select(route => new RouteState
                {
                    Id = route.Id,
                    A = route.A,
                    B = route.B,
                    TravelDays = route.TravelDays,
                    DailyCapacity = route.DailyCapacity,
                    BaseTravelDays = route.TravelDays,
                    BaseDailyCapacity = route.DailyCapacity
                })
                .ToList(),
            ScheduledEvents = content.Scenario.ScheduledEvents.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            RandomStreams = SeededRandom.CreateStreams(content.Scenario.Seed, RandomStreamNames)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, CityState> CreateCities(ContentCatalog content, Func<GridCoordinate, string> zoneId) => content.Scenario.Cities
        .OrderBy(city => city.Id, StringComparer.Ordinal)
        .ToDictionary(
            city => city.Id,
            city => new CityState
            {
                Id = city.Id,
                Name = city.Name,
                SpatialNodeId = SpatialGenerator.CitySpatialNodeId(city.Id),
                WorkerShare = city.WorkerShare,
                FoodPerPersonPerDay = city.FoodPerPersonPerDay,
                LocalReserveDays = content.Scenario.ReserveDays,
                Stocks = content.Resources.Resources.ToDictionary(
                    resource => resource.Id,
                    resource => city.Stocks.GetValueOrDefault(resource.Id),
                    StringComparer.Ordinal),
                Markets = content.Resources.Resources.ToDictionary(
                    resource => resource.Id,
                    resource => new MarketState { Price = resource.BaseValue },
                    StringComparer.Ordinal),
                Industries = city.Industries
                    .OrderBy(industry => industry.Id, StringComparer.Ordinal)
                    .Select(industry => new IndustryState
                    {
                        Id = industry.Id,
                        RecipeId = industry.RecipeId,
                        Capacity = industry.Capacity,
                        Zone = industry.Zone,
                        ZoneId = zoneId(industry.Zone),
                        InitialCapacity = industry.Capacity
                    })
                    .ToList(),
                Institutions = city.Institutions
                    .OrderBy(institution => institution.Id, StringComparer.Ordinal)
                    .Select(institution => new InstitutionState
                    {
                        Id = institution.Id,
                        Type = institution.Type,
                        Competence = institution.Competence,
                        LearningRate = institution.LearningRate,
                        Priorities = institution.Priorities.ToArray()
                    })
                    .ToList(),
                ActiveEffects = new Dictionary<string, ActiveEffectState>(StringComparer.Ordinal),
                ResourceSignals = new Dictionary<string, string>(StringComparer.Ordinal),
                KnowledgeState = new CityKnowledgeState(),
                TechnologyState = TechnologyStateFactory.Create(content, city),
                Demography = new CityDemographyState(),
                Infrastructure = new InfrastructureState(),
                Needs = content.Resources.Resources
                    .Where(resource => resource.HouseholdNeed is not null && resource.Id != "food")
                    .ToDictionary(resource => resource.Id, _ => new NeedState(), StringComparer.Ordinal),
                Shortage = new ShortageState()
            },
            StringComparer.Ordinal);

    private static Dictionary<string, ActorState> CreateActors(ContentCatalog content, SpatialHierarchy spatial, Func<GridCoordinate, string> zoneId) =>
        content.Scenario.ImportantActors
            .OrderBy(actor => actor.Id, StringComparer.Ordinal)
            .ToDictionary(
                actor => actor.Id,
                actor =>
                {
                    var territoryId = zoneId(actor.Zone);
                    var cityId = spatial.Territories[territoryId].AssignedCityId;
                    return new ActorState
                    {
                        Id = actor.Id,
                        Name = actor.Name,
                        Role = actor.Role,
                        Location = new ActorLocation(territoryId, cityId, SpatialGenerator.CitySpatialNodeId(cityId)),
                        Importance = new ActorImportance(actor.Importance, actor.Reasons.ToArray()),
                        Provenance = new ActorProvenance("scenario", null)
                    };
                },
                StringComparer.Ordinal);
}
