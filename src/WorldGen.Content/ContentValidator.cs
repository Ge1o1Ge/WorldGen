using WorldGen.Core.Content;

namespace WorldGen.Content;

public static class ContentValidator
{
    private static readonly HashSet<string> TechnologyRelationTypes = new(StringComparer.Ordinal)
    {
        "required", "helps", "enables", "substitutes", "industrial", "scientific", "supports"
    };

    private static readonly HashSet<string> SitePotentialTypes = new(StringComparer.Ordinal)
    {
        "arable", "pasture", "timber", "fish", "clay", "stone", "iron_ore"
    };

    public static void Validate(ContentCatalog content)
    {
        Schema(content.Resources.SchemaVersion, 2, "resources.schemaVersion");
        Schema(content.Recipes.SchemaVersion, 2, "recipes.schemaVersion");
        Schema(content.Technologies.SchemaVersion, 2, "technologies.schemaVersion");
        Schema(content.Map.SchemaVersion, 3, "map.schemaVersion");
        Schema(content.Scenario.SchemaVersion, 2, "scenario.schemaVersion");

        var resourceIds = UniqueIds(content.Resources.Resources, item => item.Id, "resources.resources");
        foreach (var resource in content.Resources.Resources)
        {
            NonEmpty(resource.Name, $"resources.resources.{resource.Id}.name");
            NonEmpty(resource.Unit, $"resources.resources.{resource.Id}.unit");
            NonEmpty(resource.Category, $"resources.resources.{resource.Id}.category");
            Range(resource.BaseValue, double.Epsilon, double.MaxValue, $"resources.resources.{resource.Id}.baseValue");
            Range(resource.DecayPerDay, 0, 1, $"resources.resources.{resource.Id}.decayPerDay");
            if (resource.HouseholdNeed is null) continue;
            Range(resource.HouseholdNeed.PerPersonPerDay, 0, double.MaxValue,
                $"resources.resources.{resource.Id}.householdNeed.perPersonPerDay");
            if (resource.HouseholdNeed.Seasonality is not null)
            {
                ValidateSeasonality(resource.HouseholdNeed.Seasonality,
                    $"resources.resources.{resource.Id}.householdNeed.seasonality");
            }
        }

        var recipeIds = UniqueIds(content.Recipes.Recipes, item => item.Id, "recipes.recipes");
        foreach (var recipe in content.Recipes.Recipes)
        {
            ValidateAmounts(recipe.Inputs, resourceIds, $"recipes.recipes.{recipe.Id}.inputs");
            ValidateAmounts(recipe.Outputs, resourceIds, $"recipes.recipes.{recipe.Id}.outputs");
            Require(recipe.Outputs.Count > 0, $"recipes.recipes.{recipe.Id}.outputs", "рецепт ничего не производит");
            Range(recipe.LaborPerBatch, double.Epsilon, double.MaxValue,
                $"recipes.recipes.{recipe.Id}.laborPerBatch");
            if (recipe.SitePotential is not null)
            {
                Require(SitePotentialTypes.Contains(recipe.SitePotential),
                    $"recipes.recipes.{recipe.Id}.sitePotential", "неизвестный природный потенциал");
            }
            if (recipe.Seasonality is not null)
            {
                ValidateSeasonality(recipe.Seasonality, $"recipes.recipes.{recipe.Id}.seasonality");
            }
        }

        var technologyIds = UniqueIds(content.Technologies.Technologies, item => item.Id,
            "technologies.technologies");
        foreach (var technology in content.Technologies.Technologies)
        {
            Range(technology.Complexity, 0.01, 1, $"technologies.technologies.{technology.Id}.complexity");
            Range(technology.Diffusion, 0, 1, $"technologies.technologies.{technology.Id}.diffusion");
        }
        foreach (var recipe in content.Recipes.Recipes)
        {
            foreach (var technologyId in recipe.RequiredTechnologyIds)
            {
                Require(technologyIds.Contains(technologyId), $"recipes.recipes.{recipe.Id}.requiredTechnologyIds",
                    $"неизвестная технология '{technologyId}'");
            }
        }
        foreach (var relation in content.Technologies.Relations)
        {
            Require(technologyIds.Contains(relation.From) && technologyIds.Contains(relation.To),
                "technologies.relations", "ссылка на неизвестную технологию");
            Require(TechnologyRelationTypes.Contains(relation.Type), "technologies.relations.type",
                $"неизвестный тип '{relation.Type}'");
        }
        ValidateRequiredGraph(content.Technologies);

        ValidateMap(content.Map);
        ValidateScenario(content.Scenario, content.Map, resourceIds, recipeIds, technologyIds);
    }

    private static void ValidateMap(MapDocument map)
    {
        Require(map.GeneratorVersion >= 1, "map.generatorVersion", "версия должна быть положительной");
        IntegerRange(map.Grid.Width, 10, 1000, "map.grid.width");
        IntegerRange(map.Grid.Height, 10, 1000, "map.grid.height");
        IntegerRange(map.Grid.AggregationFactor, 2, 100, "map.grid.aggregationFactor");
        Require(map.Grid.Width % map.Grid.AggregationFactor == 0 && map.Grid.Height % map.Grid.AggregationFactor == 0,
            "map.grid.aggregationFactor", "размеры сетки должны делиться на коэффициент агрегации");
        Range(map.Grid.ZoneSizeMeters, double.Epsilon, double.MaxValue, "map.grid.zoneSizeMeters");
        Range(map.Grid.VertexJitter, 0, 0.3, "map.grid.vertexJitter");
        IntegerRange(map.Population.Total, 0, int.MaxValue, "map.population.total");
        Range(map.Terrain.FertilityBase, 0, 1, "map.terrain.fertilityBase");
        Range(map.Terrain.FertilityVariation, 0, 1, "map.terrain.fertilityVariation");
        Range(map.Terrain.Roughness, 0, 1, "map.terrain.roughness");
        Range(map.Climate.Rainfall, 0, 1, "map.climate.rainfall");
        Range(map.Hydrology.RiverCenterY, 0, map.Grid.Height - 1, "map.hydrology.riverCenterY");
        Range(map.Hydrology.RiverWidthZones, 0.2, 20, "map.hydrology.riverWidthZones");
        Range(map.Hydrology.FloodplainWidthZones, map.Hydrology.RiverWidthZones, 100,
            "map.hydrology.floodplainWidthZones");
    }

    private static void ValidateScenario(
        ScenarioDocument scenario,
        MapDocument map,
        HashSet<string> resourceIds,
        HashSet<string> recipeIds,
        HashSet<string> technologyIds)
    {
        IntegerRange(scenario.Calendar.DaysPerYear, 1, 1000, "scenario.calendar.daysPerYear");
        IntegerRange(scenario.ReserveDays, 1, int.MaxValue, "scenario.reserveDays");
        Range(scenario.Demography.BirthRatePerYear, 0, 1, "scenario.demography.birthRatePerYear");
        Range(scenario.Demography.DeathRatePerYear, 0, 1, "scenario.demography.deathRatePerYear");
        Range(scenario.Demography.MonthlyMigrationShare, 0, 1, "scenario.demography.monthlyMigrationShare");

        var cityIds = UniqueIds(scenario.Cities, item => item.Id, "scenario.cities");
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var city in scenario.Cities)
        {
            ValidateCoordinate(city.Anchor, map, $"scenario.cities.{city.Id}.anchor");
            Require(anchors.Add($"{city.Anchor.X}:{city.Anchor.Y}"), $"scenario.cities.{city.Id}.anchor",
                "зона уже является якорем другого города");
            Range(city.WorkerShare, 0, 1, $"scenario.cities.{city.Id}.workerShare");
            Range(city.FoodPerPersonPerDay, 0, double.MaxValue,
                $"scenario.cities.{city.Id}.foodPerPersonPerDay");
            ValidateAmounts(city.Stocks, resourceIds, $"scenario.cities.{city.Id}.stocks");
            UniqueIds(city.Industries, item => item.Id, $"scenario.cities.{city.Id}.industries");
            foreach (var industry in city.Industries)
            {
                Require(recipeIds.Contains(industry.RecipeId), $"scenario.cities.{city.Id}.industries.{industry.Id}",
                    "неизвестный рецепт");
                Range(industry.Capacity, 0, double.MaxValue,
                    $"scenario.cities.{city.Id}.industries.{industry.Id}.capacity");
                ValidateCoordinate(industry.Zone, map, $"scenario.cities.{city.Id}.industries.{industry.Id}.zone");
            }
            UniqueIds(city.Institutions, item => item.Id, $"scenario.cities.{city.Id}.institutions");
            foreach (var seed in city.TechnologySeeds)
            {
                Require(technologyIds.Contains(seed.Key), $"scenario.cities.{city.Id}.technologySeeds",
                    $"неизвестная технология '{seed.Key}'");
                Require(seed.Value.Length == 4, $"scenario.cities.{city.Id}.technologySeeds.{seed.Key}",
                    "ожидались Knowledge/Competence/Capability/Adoption");
                foreach (var value in seed.Value)
                {
                    Range(value, 0, 1, $"scenario.cities.{city.Id}.technologySeeds.{seed.Key}");
                }
            }
        }

        UniqueIds(scenario.ImportantActors, item => item.Id, "scenario.importantActors");
        foreach (var actor in scenario.ImportantActors)
        {
            ValidateCoordinate(actor.Zone, map, $"scenario.importantActors.{actor.Id}.zone");
            Range(actor.Importance, 0, 1, $"scenario.importantActors.{actor.Id}.importance");
        }
        UniqueIds(scenario.Routes, item => item.Id, "scenario.routes");
        foreach (var route in scenario.Routes)
        {
            Require(cityIds.Contains(route.A) && cityIds.Contains(route.B) && route.A != route.B,
                $"scenario.routes.{route.Id}", "маршрут должен соединять разные известные города");
            IntegerRange(route.TravelDays, 1, int.MaxValue, $"scenario.routes.{route.Id}.travelDays");
            Range(route.DailyCapacity, double.Epsilon, double.MaxValue,
                $"scenario.routes.{route.Id}.dailyCapacity");
        }
    }

    private static void ValidateRequiredGraph(TechnologyDocument document)
    {
        var outgoing = document.Technologies.ToDictionary(
            technology => technology.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var relation in document.Relations.Where(relation => relation.Type == "required"))
        {
            outgoing[relation.From].Add(relation.To);
        }
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            Require(!visiting.Contains(id), "technologies.relations", $"цикл required-связей около '{id}'");
            if (visited.Contains(id)) return;
            visiting.Add(id);
            foreach (var target in outgoing[id]) Visit(target);
            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var technology in document.Technologies) Visit(technology.Id);
    }

    private static HashSet<string> UniqueIds<T>(IEnumerable<T> items, Func<T, string> id, string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var value = id(item);
            NonEmpty(value, $"{path}.id");
            Require(result.Add(value), $"{path}.id", $"повторяющийся id '{value}'");
        }
        return result;
    }

    private static void ValidateAmounts(
        IReadOnlyDictionary<string, double> amounts,
        HashSet<string> resourceIds,
        string path)
    {
        foreach (var amount in amounts)
        {
            Require(resourceIds.Contains(amount.Key), $"{path}.{amount.Key}", "неизвестный ресурс");
            Range(amount.Value, 0, double.MaxValue, $"{path}.{amount.Key}");
        }
    }

    private static void ValidateSeasonality(SeasonalityDefinition seasonality, string path)
    {
        Range(seasonality.Minimum, 0, 1, $"{path}.minimum");
        IntegerRange(seasonality.PeakDay, 0, 364, $"{path}.peakDay");
    }

    private static void ValidateCoordinate(GridCoordinate coordinate, MapDocument map, string path)
    {
        IntegerRange(coordinate.X, 0, map.Grid.Width - 1, $"{path}.x");
        IntegerRange(coordinate.Y, 0, map.Grid.Height - 1, $"{path}.y");
    }

    private static void Schema(int actual, int expected, string path) =>
        Require(actual == expected, path, $"поддерживается только версия {expected}");

    private static void NonEmpty(string value, string path) =>
        Require(!string.IsNullOrWhiteSpace(value), path, "ожидалась непустая строка");

    private static void IntegerRange(int value, int minimum, int maximum, string path) =>
        Require(value >= minimum && value <= maximum, path, $"ожидалось целое число от {minimum} до {maximum}");

    private static void Range(double value, double minimum, double maximum, string path) =>
        Require(double.IsFinite(value) && value >= minimum && value <= maximum, path,
            $"ожидалось конечное число от {minimum} до {maximum}");

    private static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new ContentValidationException(path, message);
    }
}
