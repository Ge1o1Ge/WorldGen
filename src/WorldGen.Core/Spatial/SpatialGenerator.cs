using WorldGen.Core.Content;

namespace WorldGen.Core.Spatial;

public static class SpatialGenerator
{
    public static string ZoneId(int x, int y) => $"zone:{x}:{y}";

    public static string MacroNodeId(int x, int y) => $"macro:{x}:{y}";

    public static string CitySpatialNodeId(string cityId) => $"city:{cityId}";

    public static SpatialHierarchy Build(ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var map = content.Map;
        var scenario = content.Scenario;
        var grid = map.Grid;
        var citiesById = scenario.Cities.ToDictionary(city => city.Id, StringComparer.Ordinal);
        var zones = new List<Territory>(grid.Width * grid.Height);

        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var geography = ClassifyZone(map, x, y);
                var id = ZoneId(x, y);
                zones.Add(new Territory
                {
                    Id = id,
                    Kind = "territory",
                    Name = $"Зона {x}:{y}",
                    Grid = new GridPosition(x, y),
                    Area = grid.ZoneSizeMeters * grid.ZoneSizeMeters,
                    Population = 0,
                    ElevationMeters = geography.ElevationMeters,
                    Slope = geography.Slope,
                    TemperatureC = geography.TemperatureC,
                    Moisture = geography.Moisture,
                    Fertility = geography.Fertility,
                    ForestCover = geography.ForestCover,
                    Biome = geography.Biome,
                    Terrain = geography.Terrain,
                    Water = geography.Water,
                    ResourcePotential = geography.ResourcePotential,
                    AssignedCityId = AssignCity(x, y, scenario.Cities, grid.Seed),
                    ParentNodeId = MacroNodeId(x / grid.AggregationFactor, y / grid.AggregationFactor),
                    TriangleIds = [$"{id}:a", $"{id}:b"],
                    Diagonal = ((unchecked((uint)(x + y)) + grid.Seed) & 1) == 0 ? "nw-se" : "ne-sw",
                    NaturalState = new NaturalState
                    {
                        SoilQuality = geography.Fertility,
                        ForestBiomass = geography.ForestCover,
                        FishStock = geography.ResourcePotential["fish"],
                        Deposits = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["clay"] = geography.ResourcePotential["clay"] > 0 ? 1 : 0,
                            ["stone"] = geography.ResourcePotential["stone"] > 0 ? 1 : 0,
                            ["iron_ore"] = geography.ResourcePotential["iron_ore"] > 0 ? 1 : 0
                        },
                        ExtractedBatches = new Dictionary<string, double>(StringComparer.Ordinal)
                    }
                });
            }
        }

        DistributePopulation(zones, map.Population.Total, citiesById, map);
        var territories = zones.ToDictionary(zone => zone.Id, StringComparer.Ordinal);
        var regionNodeId = $"region:{scenario.Id}";
        var nodes = new Dictionary<string, SpatialNode>(StringComparer.Ordinal);
        var macroWidth = grid.Width / grid.AggregationFactor;
        var macroHeight = grid.Height / grid.AggregationFactor;

        for (var my = 0; my < macroHeight; my++)
        {
            for (var mx = 0; mx < macroWidth; mx++)
            {
                var childTerritoryIds = new List<string>(grid.AggregationFactor * grid.AggregationFactor);
                for (var y = my * grid.AggregationFactor; y < (my + 1) * grid.AggregationFactor; y++)
                {
                    for (var x = mx * grid.AggregationFactor; x < (mx + 1) * grid.AggregationFactor; x++)
                    {
                        childTerritoryIds.Add(ZoneId(x, y));
                    }
                }

                var children = childTerritoryIds.Select(id => territories[id]).ToArray();
                var nodeId = MacroNodeId(mx, my);
                nodes[nodeId] = new SpatialNode
                {
                    Id = nodeId,
                    Kind = "macro",
                    Grid = new GridPosition(mx, my),
                    ParentNodeId = regionNodeId,
                    ChildTerritoryIds = childTerritoryIds,
                    DominantCityId = DominantCity(children),
                    Aggregate = AggregateTerritories(children),
                    Detail = null,
                    ActiveUntilDay = null
                };
            }
        }

        var macroNodeIds = nodes.Values
            .Where(node => node.Kind == "macro")
            .Select(node => node.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        nodes[regionNodeId] = new SpatialNode
        {
            Id = regionNodeId,
            Kind = "region",
            WorldEntityId = scenario.Id,
            Name = map.Name,
            ParentNodeId = null,
            ChildNodeIds = macroNodeIds,
            OverlayNodeIds = scenario.Cities.Select(city => CitySpatialNodeId(city.Id)).Order(StringComparer.Ordinal).ToArray(),
            Aggregate = AggregateTerritories(zones)
        };

        foreach (var city in scenario.Cities.OrderBy(city => city.Id, StringComparer.Ordinal))
        {
            var childTerritoryIds = zones
                .Where(zone => zone.AssignedCityId == city.Id)
                .Select(zone => zone.Id)
                .ToArray();
            var nodeId = CitySpatialNodeId(city.Id);
            nodes[nodeId] = new SpatialNode
            {
                Id = nodeId,
                Kind = "city",
                Projection = "settlement",
                WorldEntityId = city.Id,
                Name = city.Name,
                ParentNodeId = regionNodeId,
                AnchorTerritoryId = ZoneId(city.Anchor.X, city.Anchor.Y),
                ChildTerritoryIds = childTerritoryIds,
                Aggregate = AggregateTerritories(childTerritoryIds.Select(id => territories[id])),
                Detail = null,
                ActiveUntilDay = null
            };
        }

        return new SpatialHierarchy
        {
            RegionNodeId = regionNodeId,
            Grid = new SpatialGrid
            {
                Width = grid.Width,
                Height = grid.Height,
                ZoneSizeMeters = grid.ZoneSizeMeters,
                AggregationFactor = grid.AggregationFactor,
                MacroWidth = macroWidth,
                MacroHeight = macroHeight,
                VertexJitter = grid.VertexJitter,
                Seed = grid.Seed,
                GeneratorVersion = map.GeneratorVersion,
                Levels =
                [
                    new SpatialLevel(0, "zone", grid.Width, grid.Height, 1),
                    new SpatialLevel(1, "macro", macroWidth, macroHeight, grid.AggregationFactor),
                    new SpatialLevel(2, "region", 1, 1, grid.Width)
                ]
            },
            Territories = territories,
            Nodes = nodes
        };
    }

    private static ZoneGeography ClassifyZone(MapDocument map, int x, int y)
    {
        var elevationMeters = SampleElevation(map, x, y);
        var neighborElevations = new[]
        {
            SampleElevation(map, x - 1, y), SampleElevation(map, x + 1, y),
            SampleElevation(map, x, y - 1), SampleElevation(map, x, y + 1)
        };
        var maxRise = neighborElevations.Max(value => Math.Abs(value - elevationMeters));
        var slope = Clamp(maxRise / Math.Max(1, map.Grid.ZoneSizeMeters * 0.45), 0, 1);
        var distanceToRiver = Math.Abs(y + 0.5 - RiverCenter(map, x + 0.5));
        var river = distanceToRiver <= map.Hydrology.RiverWidthZones / 2;
        var floodplain = !river && distanceToRiver <= map.Hydrology.FloodplainWidthZones;
        var rainfallNoise = FractalNoise(map.Grid.Seed, x, y, 307);
        var riverMoisture = Math.Exp(-distanceToRiver / 7) * 0.48;
        var moisture = Clamp(map.Climate.Rainfall * 0.55 + rainfallNoise * 0.35 + riverMoisture - slope * 0.12, 0, 1);
        var temperatureC = map.Climate.MeanTemperatureC +
            (ValueNoise(map.Grid.Seed, x, y, 29, 401) - 0.5) * map.Climate.TemperatureRangeC -
            Math.Max(0, elevationMeters - map.Terrain.ElevationBaseMeters) * 0.0065;
        var fertilityNoise = FractalNoise(map.Grid.Seed, x, y, 41);
        var fertility = river ? 0 : Clamp(
            map.Terrain.FertilityBase + (fertilityNoise * 2 - 1) * map.Terrain.FertilityVariation +
            (floodplain ? 0.18 : 0) - slope * 0.28, 0, 1);
        var rawForestCover = river ? 0 : Clamp((moisture - 0.38) * 2.5 +
            (FractalNoise(map.Grid.Seed, x, y, 601) - 0.5) * 0.68 - (floodplain ? 0.16 : 0), 0, 1);
        var wetland = !river && distanceToRiver < map.Hydrology.FloodplainWidthZones * 0.65 && moisture > 0.78;
        var forestCover = wetland ? Math.Min(0.45, rawForestCover) : rawForestCover;

        string biome;
        if (river) biome = "river";
        else if (wetland) biome = "wetland";
        else if (elevationMeters > map.Terrain.ElevationBaseMeters + map.Terrain.ElevationRangeMeters * 0.43 && forestCover > 0.42) biome = "upland_forest";
        else if (forestCover > 0.58) biome = "forest";
        else if (floodplain) biome = "floodplain";
        else if (moisture < 0.38) biome = "dry_grassland";
        else biome = "meadow";

        var stoneSignal = Clamp(slope * 1.45 +
            (elevationMeters - map.Terrain.ElevationBaseMeters) / map.Terrain.ElevationRangeMeters * 0.45, 0, 1);
        var ironNoise = Hash01(map.Grid.Seed, x / 4, y / 4, 911) * 0.72 +
            FractalNoise(map.Grid.Seed, x, y, 977) * 0.28;
        var potentials = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["arable"] = river || wetland ? 0 : Clamp(fertility * (1 - slope) * (1 - forestCover * 0.55), 0, 1),
            ["pasture"] = river || wetland ? 0 : Clamp((1 - forestCover) * (0.45 + fertility * 0.45) * (1 - slope * 0.55), 0, 1),
            ["timber"] = forestCover,
            ["fish"] = river ? 1 : Clamp(Math.Exp(-distanceToRiver / 2.8) * 0.38, 0, 1),
            ["clay"] = river ? 0.25 : Clamp((floodplain ? 0.55 : 0.08) + moisture * 0.25 - slope * 0.5, 0, 1),
            ["stone"] = stoneSignal,
            ["iron_ore"] = Clamp((ironNoise - 0.45) * 2.4, 0, 1) * (0.35 + stoneSignal * 0.65)
        };
        var roundedPotentials = potentials.ToDictionary(pair => pair.Key, pair => RoundJs(pair.Value, 1000), StringComparer.Ordinal);

        return new ZoneGeography(
            RoundJs(elevationMeters, 10),
            RoundJs(slope, 1000),
            RoundJs(temperatureC, 10),
            RoundJs(moisture, 1000),
            RoundJs(fertility, 1000),
            RoundJs(forestCover, 1000),
            biome,
            river ? "water" : wetland ? "marsh" : slope > 0.42 ? "hills" : "plains",
            new WaterState(river, floodplain, RoundJs(distanceToRiver, 100)),
            roundedPotentials);
    }

    private static string AssignCity(int x, int y, IReadOnlyList<CityDefinition> cities, uint seed)
    {
        var anchor = cities.FirstOrDefault(city => city.Anchor.X == x && city.Anchor.Y == y);
        if (anchor is not null)
        {
            return anchor.Id;
        }

        var bestScore = double.PositiveInfinity;
        string? bestCityId = null;
        for (var index = 0; index < cities.Count; index++)
        {
            var city = cities[index];
            var distance = Math.Abs(x - city.Anchor.X) + Math.Abs(y - city.Anchor.Y);
            var warp = (ValueNoise(seed, x, y, 13, 1000 + index * 97) - 0.5) * 8;
            var score = distance + warp;
            if (score < bestScore || (score == bestScore && StringComparer.Ordinal.Compare(city.Id, bestCityId) < 0))
            {
                bestScore = score;
                bestCityId = city.Id;
            }
        }

        return bestCityId ?? throw new InvalidOperationException("Невозможно назначить территорию: в сценарии нет поселений");
    }

    private static void DistributePopulation(
        IReadOnlyList<Territory> zones,
        int totalPopulation,
        IReadOnlyDictionary<string, CityDefinition> citiesById,
        MapDocument map)
    {
        var weighted = zones.Select(zone =>
        {
            var city = citiesById[zone.AssignedCityId];
            var dx = zone.Grid.X - city.Anchor.X;
            var dy = zone.Grid.Y - city.Anchor.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var urban = map.Population.UrbanConcentration * Math.Exp(-distance / map.Population.UrbanRadius);
            var weight = zone.Water.River ? 0 : 0.15 + zone.Fertility * 0.35 + urban;
            return new PopulationWeight(zone, weight);
        }).ToArray();
        var totalWeight = weighted.Sum(item => item.Weight);
        var assigned = 0;
        var fractions = new List<PopulationFraction>(zones.Count);

        foreach (var item in weighted)
        {
            var exact = totalPopulation * item.Weight / totalWeight;
            item.Zone.Population = (int)Math.Floor(exact);
            assigned += item.Zone.Population;
            fractions.Add(new PopulationFraction(item.Zone, exact - item.Zone.Population));
        }

        fractions.Sort((left, right) =>
        {
            var fractionComparison = right.Fraction.CompareTo(left.Fraction);
            return fractionComparison != 0
                ? fractionComparison
                : StringComparer.InvariantCulture.Compare(left.Zone.Id, right.Zone.Id);
        });
        for (var index = 0; index < totalPopulation - assigned; index++)
        {
            fractions[index].Zone.Population++;
        }
    }

    public static SpatialAggregate AggregateTerritories(IEnumerable<Territory> source)
    {
        var territories = source as IReadOnlyList<Territory> ?? source.ToArray();
        var area = territories.Sum(territory => territory.Area);
        var count = territories.Count;
        var resources = territories
            .SelectMany(territory => territory.ResourcePotential.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                resourceId => resourceId,
                resourceId => territories.Sum(territory => territory.ResourcePotential[resourceId]) / Math.Max(1, count),
                StringComparer.Ordinal);
        var biomeShares = territories
            .GroupBy(territory => territory.Biome, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count() / (double)Math.Max(1, count), StringComparer.Ordinal);

        return new SpatialAggregate
        {
            Area = area,
            Population = territories.Sum(territory => territory.Population),
            Fertility = area == 0 ? 0 : territories.Sum(territory => territory.Fertility * territory.Area) / area,
            MeanElevationMeters = territories.Sum(territory => territory.ElevationMeters) / Math.Max(1, count),
            MeanMoisture = territories.Sum(territory => territory.Moisture) / Math.Max(1, count),
            ResourcePotential = resources,
            BiomeShares = biomeShares
        };
    }

    public static string DominantCity(IEnumerable<Territory> territories) => territories
        .GroupBy(territory => territory.AssignedCityId, StringComparer.Ordinal)
        .Select(group => new { CityId = group.Key, Count = group.Count() })
        .OrderByDescending(item => item.Count)
        .ThenBy(item => item.CityId, StringComparer.Ordinal)
        .First()
        .CityId;

    private static double SampleElevation(MapDocument map, double x, double y)
    {
        var noise = FractalNoise(map.Grid.Seed, x, y, 83);
        var riverDistance = Math.Abs(y - RiverCenter(map, x));
        var valley = Math.Exp(-riverDistance / 8) * map.Terrain.ElevationRangeMeters * 0.22;
        var regionalTilt = (x / map.Grid.Width - 0.5) * map.Terrain.ElevationRangeMeters * 0.12;
        return map.Terrain.ElevationBaseMeters +
            noise * map.Terrain.ElevationRangeMeters * map.Terrain.Roughness + regionalTilt - valley;
    }

    private static double RiverCenter(MapDocument map, double x)
    {
        var broad = Math.Sin((x + map.Grid.Seed % 19) / 14) * map.Hydrology.Meander * 0.5;
        var local = (ValueNoise(map.Grid.Seed, x, 0, 21, 503) - 0.5) * map.Hydrology.Meander;
        return map.Hydrology.RiverCenterY + broad + local;
    }

    private static double FractalNoise(uint seed, double x, double y, int salt = 0) =>
        ValueNoise(seed, x, y, 37, salt) * 0.52 +
        ValueNoise(seed, x, y, 17, salt + 101) * 0.3 +
        ValueNoise(seed, x, y, 7, salt + 211) * 0.18;

    private static double ValueNoise(uint seed, double x, double y, double scale, int salt = 0)
    {
        var gx = (int)Math.Floor(x / scale);
        var gy = (int)Math.Floor(y / scale);
        var tx = Smoothstep(x / scale - gx);
        var ty = Smoothstep(y / scale - gy);
        var n00 = Hash01(seed, gx, gy, salt);
        var n10 = Hash01(seed, gx + 1, gy, salt);
        var n01 = Hash01(seed, gx, gy + 1, salt);
        var n11 = Hash01(seed, gx + 1, gy + 1, salt);
        var top = n00 + (n10 - n00) * tx;
        var bottom = n01 + (n11 - n01) * tx;
        return top + (bottom - top) * ty;
    }

    private static double Hash01(uint seed, int x, int y, int salt = 0)
    {
        var value = seed ^
            unchecked((uint)(x + 1) * 0x9e3779b1u) ^
            unchecked((uint)(y + 1) * 0x85ebca77u) ^
            unchecked((uint)salt);
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846ca68bu);
        value ^= value >> 16;
        return value / 4294967296d;
    }

    private static double Smoothstep(double value) => value * value * (3 - 2 * value);

    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));

    private static double RoundJs(double value, double factor) => Math.Floor(value * factor + 0.5) / factor;

    private sealed record ZoneGeography(
        double ElevationMeters,
        double Slope,
        double TemperatureC,
        double Moisture,
        double Fertility,
        double ForestCover,
        string Biome,
        string Terrain,
        WaterState Water,
        IReadOnlyDictionary<string, double> ResourcePotential);

    private sealed record PopulationWeight(Territory Zone, double Weight);

    private sealed record PopulationFraction(Territory Zone, double Fraction);
}
