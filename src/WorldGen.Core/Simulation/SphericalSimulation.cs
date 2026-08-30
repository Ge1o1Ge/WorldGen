using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Core.Content;
using WorldGen.Core.Serialization;
using WorldGen.Core.Settlements;
using WorldGen.Core.Spatial;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

/// <summary>Runs the existing economic engine on sparse, genuinely spherical sites.
/// The regional world is not copied or stepped. Atlas coordinates are compatibility
/// addresses only; identity, adjacency and terrain come from CellAddress/topology.</summary>
public sealed class SphericalSimulation
{
    private readonly SphericalSettlementLayer settlements;
    public ContentCatalog Content { get; }
    public WorldState World { get; }
    public IReadOnlyDictionary<string, SphericalIndustrySite> Sites { get; }
    public IReadOnlyDictionary<string, CellAddress> Addresses { get; }
    public IReadOnlyList<string> Warnings { get; }
    public SettlementSimulation? Development { get; private set; }

    private SphericalSimulation(ContentCatalog content, WorldState world, SphericalSettlementLayer settlements,
        Dictionary<string, SphericalIndustrySite> sites, Dictionary<string, CellAddress> addresses, List<string> warnings)
    { Content = content; World = world; this.settlements = settlements; Sites = sites; Addresses = addresses; Warnings = warnings; }

    public void Advance(int days, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(days));
        for (var day = 0; day < days; day++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usage = Sites.ToDictionary(pair => pair.Key, pair => pair.Value.BlockedReason is not null ? 0d :
                pair.Value.LandId is { } id ? settlements.UsedLands.First(land => land.Id == id).Usage : 1d, StringComparer.Ordinal);
            SimulationEngine.Step(World, Content, usage, Development);
            Development?.ReconcileFoodComposition();
            Development?.RehousePopulation();
        }
    }

    public float? ForestAt(CellAddress cell) => World.Spatial.Territories.TryGetValue(ZoneId(cell), out var territory)
        ? (float)territory.NaturalState.ForestBiomass : null;
    public static string ZoneId(CellAddress cell) => $"sphere:{cell.Face}:{cell.X}:{cell.Y}";

    public static SphericalSimulation Create(ContentCatalog source, SphericalWorldDefinition definition,
        SphericalEconomyDefinition economy, CubeSphereTopology topology, SphericalTerrainGenerator terrain,
        SphericalHydrology hydro, SphericalSettlementLayer settlements, SettlementRules? rules = null, JsonObject? snapshot = null)
    {
        if (economy.Cities.Select(city => city.SettlementId).Distinct().Count() != economy.Cities.Count ||
            economy.Cities.SelectMany(city => city.SourceCities).Distinct().Count() != economy.Cities.Sum(city => city.SourceCities.Count))
            throw new InvalidOperationException("Повторяющееся поселение или шаблон экономики");
        var recipes = source.Recipes.Recipes.ToDictionary(recipe => recipe.Id);
        if (economy.Stage == "foragers" && rules is null) throw new InvalidOperationException("Для стоянок нужны правила домохозяйств");
        if (rules is not null)
        {
            rules.Validate();
            if (rules.Primitive is { } era)
            {
                var ids = era.Technologies.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                source = source with
                {
                    Resources = source.Resources with { Resources = source.Resources.Resources.Select(r => r.Id == "food" ? r with { DecayPerDay = era.FreshFoodDecay } : r).ToArray() },
                    Technologies = source.Technologies with
                    {
                        Technologies = source.Technologies.Technologies.Where(t => !ids.Contains(t.Id)).Concat(era.Technologies.Select(t => new TechnologyDefinition
                        { Id = t.Id, Name = t.Name, Domain = t.Domain, Complexity = 1, Diffusion = .01 })).ToArray(),
                        Relations = source.Technologies.Relations
                            .Concat(era.Technologies.SelectMany(t => t.Prerequisites.Select(p => new TechnologyRelation { From = p, To = t.Id, Type = "required" })))
                            .Concat(era.Technologies.SelectMany(t => t.AlternativePrerequisites.Select(p => new TechnologyRelation { From = p, To = t.Id, Type = "alternative" })))
                            .Concat(era.Relations)
                            .GroupBy(relation => (relation.From, relation.To, relation.Type)).Select(group => group.First()).ToArray()
                    }
                };
            }
            var resourceIds = source.Resources.Resources.Select(r => r.Id).Concat(rules.Resources.Select(r => r.Id)).ToArray();
            if (resourceIds.Distinct().Count() != resourceIds.Length || rules.Activities.Any(a => !resourceIds.Contains(a.Output) || a.Inputs.Keys.Any(id => !resourceIds.Contains(id))) ||
                rules.Buildings.Any(b => b.Materials.Keys.Any(id => !resourceIds.Contains(id))) ||
                rules.Lifecycle is { } lifecycle && lifecycle.Materials.Any(m => m.Materials.Keys.Any(id => !resourceIds.Contains(id))) ||
                rules.Primitive?.Processes.Any(p => p.Inputs.Keys.Concat(p.RequiredStocks.Keys).Concat(p.Outputs.Keys).Any(id => !resourceIds.Contains(id))) == true)
                throw new InvalidOperationException("Повторяющийся ресурс или неизвестная ссылка в правилах домохозяйств");
            source = source with { Resources = source.Resources with { Resources = source.Resources.Resources.Concat(rules.Resources).ToArray() } };
        }
        var sites = new Dictionary<string, SphericalIndustrySite>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, CellAddress>(StringComparer.Ordinal);
        var samples = new Dictionary<CellAddress, SiteTerrain>();
        var warnings = new List<string>();
        var cities = new List<CityDefinition>();
        var occupied = new HashSet<CellAddress>();
        var templatesToCities = new Dictionary<string, string>(StringComparer.Ordinal);
        GridCoordinate Atlas(CellAddress cell) => new() { X = (int)cell.Face * definition.FaceSize + cell.X, Y = cell.Y };
        CellAddress FromAtlas(GridCoordinate coordinate) => new((CubeFace)(coordinate.X / definition.FaceSize), coordinate.X % definition.FaceSize, coordinate.Y);
        string Macro(CellAddress cell) => $"sphere-macro:{cell.Face}:{cell.X / definition.ChunkSize}:{cell.Y / definition.ChunkSize}";

        SiteTerrain Sample(CellAddress cell)
        {
            if (samples.TryGetValue(cell, out var cached)) return cached;
            var value = terrain.GenerateCell(cell);
            var waterCell = hydro.Topology.Locate(topology.ToUnitVector(cell));
            var index = hydro.Index(waterCell);
            var neighbors = hydro.GetDrainageNeighbors(waterCell).Select(hydro.Index).ToArray();
            // For the exact grid this is IsWater(index). Retain the old containing
            // cell semantics when an explicitly coarse regression fixture is used.
            var wet = value.Biome == SphericalBiome.Ocean || hydro.Surface[index] - hydro.Elevation[index] > SphericalHydrology.LakeDepthThreshold;
            var nearWater = hydro.IsWater(index) || hydro.IsRiver(index) || neighbors.Any(i => hydro.IsWater(i) || hydro.IsRiver(i));
            var stream = hydro.IsRiver(index) || neighbors.Any(hydro.IsRiver);
            var freshWater = hydro.IsFreshWater(index) || neighbors.Any(hydro.IsFreshWater);
            var potentials = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["arable"] = wet ? 0 : value.Fertility * (1 - value.ForestCover * .55),
                ["pasture"] = wet ? 0 : (1 - value.ForestCover) * (.45 + value.Fertility * .45),
                ["timber"] = wet ? 0 : value.ForestCover,
                ["fish"] = nearWater ? 1 : 0,
                ["clay"] = wet ? .15 : Math.Clamp(value.Moisture * .7, 0, 1),
                ["stone"] = Math.Clamp((value.ElevationMeters - hydro.SeaLevel) / 550, 0, 1),
                ["iron_ore"] = Math.Clamp((value.ElevationMeters - hydro.SeaLevel) / 650, 0, 1)
            };
            // Scouts can leave the economic footprint. This cache is derived data,
            // not persistent world knowledge, and must not grow with every journey.
            if (samples.Count >= 16384) samples.Clear();
            return samples[cell] = new SiteTerrain(value, wet, nearWater, stream, freshWater, potentials);
        }

        foreach (var assignment in economy.Cities.OrderBy(city => city.SettlementId, StringComparer.Ordinal))
        {
            var settlement = definition.Settlements.Single(city => city.Id == assignment.SettlementId);
            var anchorAllocation = settlement.Buildings.First().Footprint.First();
            var anchor = new CellAddress(anchorAllocation.Face, anchorAllocation.X, anchorAllocation.Y);
            var templates = assignment.SourceCities.Select(id => source.Scenario.Cities.Single(city => city.Id == id)).ToArray();
            foreach (var template in templates) templatesToCities.Add(template.Id, settlement.Id);
            var candidates = settlements.Influence.Cells.Where(pair => pair.Value.CityId == settlement.Id)
                .Select(pair => pair.Key).Where(cell => !Sample(cell).Wet).OrderBy(cell => ZoneId(cell), StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException($"Нет суши для {settlement.Id}");
            var industries = new List<IndustryDefinition>();
            foreach (var industry in templates.SelectMany(city => city.Industries).Where(_ => economy.Stage != "foragers").OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var recipe = recipes[industry.RecipeId];
                var landKind = recipe.SitePotential switch { "arable" => CityAssetKind.CultivatedField, "pasture" => CityAssetKind.Pasture, _ => (CityAssetKind?)null };
                var land = landKind is null ? null : settlements.UsedLands.Where(land => land.CityId == settlement.Id && land.Kind == landKind && !occupied.Contains(land.Cell))
                    .OrderBy(land => land.Id, StringComparer.Ordinal).FirstOrDefault();
                var needsRiver = recipe.RequiredTechnologyIds.Contains("water_mill");
                bool Available(CellAddress cell) => !occupied.Contains(cell) && settlements.Construction.GetOccupiedCapacity(cell) < settlements.Construction.GetCapacity(cell);
                var suitable = candidates.Where(cell => Available(cell) && (!needsRiver || Sample(cell).Stream) &&
                    (recipe.SitePotential != "fish" || Sample(cell).NearWater)).ToArray();
                string? blocked = null;
                if (suitable.Length == 0) { suitable = candidates.Where(Available).ToArray(); blocked = "Нет доступного участка у воды"; }
                var cell = land?.Cell ?? suitable.OrderByDescending(cell => recipe.SitePotential is { } potential ? Sample(cell).Potentials[potential] :
                        topology.ToUnitVector(cell).Dot(topology.ToUnitVector(anchor)))
                    .ThenBy(ZoneId, StringComparer.Ordinal).First();
                occupied.Add(cell);
                var id = $"{settlement.Id}:{industry.Id}";
                if (blocked is not null) warnings.Add($"{id}: {blocked}; производство остановлено");
                sites[id] = new SphericalIndustrySite(id, settlement.Id, cell, land?.Id, blocked);
                industries.Add(industry with { Id = id, Zone = Atlas(cell) });
                // A worksite is seeded explicitly; it is not silently counted as a full urban house.
                if (land is null) settlements.Construction.Place(new BuildingPlacement($"worksite:{id}", settlement.Id,
                    "worksite", [new CellCapacityAllocation(cell, 1)], .35f));
            }
            var stocks = source.Resources.Resources.ToDictionary(resource => resource.Id,
                resource => templates.Sum(city => city.Stocks.GetValueOrDefault(resource.Id)), StringComparer.Ordinal);
            var seeds = source.Technologies.Technologies.ToDictionary(technology => technology.Id,
                technology => Enumerable.Range(0, 4).Select(i => templates.Average(city => city.TechnologySeeds.TryGetValue(technology.Id, out var seed) ? seed[i] : 0)).ToArray(), StringComparer.Ordinal);
            if (economy.Stage == "foragers")
            {
                foreach (var key in stocks.Keys.ToArray()) stocks[key] = 0;
                stocks["food"] = assignment.Population * .001 * 5;
                stocks["firewood"] = assignment.Population * .00022 * 5;
                stocks["water"] = assignment.Population * .005;
                stocks["timber"] = .5;
                foreach (var key in seeds.Keys.ToArray()) seeds[key] = [0, 0, 0, 0];
                if (rules?.Primitive is { } era)
                {
                    var known = era.Technologies.Where(t => t.Baseline).Select(t => t.Id).Concat(assignment.InitialTechnologies ?? []).ToHashSet(StringComparer.Ordinal);
                    if (known.Any(id => !era.Technologies.Any(t => t.Id == id))) throw new InvalidOperationException("Неизвестная стартовая технология");
                    void Learn(string id)
                    {
                        foreach (var p in era.Technologies.Single(t => t.Id == id).Prerequisites) { known.Add(p); Learn(p); }
                        seeds[id] = [1, .5, 0, 0];
                    }
                    foreach (var id in known.ToArray()) Learn(id);
                    stocks["stone_kit"] = assignment.Population * .08;
                    stocks["garments"] = assignment.Population;
                    stocks["primitive_bow"] = known.Contains("archery") ? assignment.Population * .02 : 0;
                    stocks["stone"] = .03; stocks["fiber"] = .03; stocks["clay"] = .1;
                }
            }
            cities.Add(templates[0] with
            {
                Id = settlement.Id,
                Name = settlement.Name,
                Anchor = Atlas(anchor),
                Stocks = stocks,
                Industries = industries,
                Institutions = economy.Stage == "foragers" ? [] : templates.SelectMany(city => city.Institutions).ToArray(),
                TechnologySeeds = seeds
            });
        }

        var regionId = $"sphere-region:{economy.Id}";
        var territories = new Dictionary<string, Territory>(StringComparer.Ordinal);
        var claimed = settlements.Influence.Cells.ToDictionary(pair => pair.Key, pair => pair.Value.CityId);
        foreach (var site in sites.Values) claimed[site.Cell] = site.CityId;
        foreach (var city in cities) claimed[FromAtlas(city.Anchor)] = city.Id;
        Territory CreateTerritory(CellAddress cell,string cityId)
        {
            var id = ZoneId(cell);
            var s = Sample(cell); var value = s.Value;
            return new Territory
            {
                Id = id,
                Kind = "territory",
                Name = id,
                Grid = new GridPosition((int)cell.Face * definition.FaceSize + cell.X, cell.Y),
                Area = definition.ZoneSizeMeters * definition.ZoneSizeMeters,
                Population = 0,
                ElevationMeters = value.ElevationMeters,
                Slope = 0,
                TemperatureC = value.TemperatureC,
                Moisture = value.Moisture,
                Fertility = value.Fertility,
                ForestCover = value.ForestCover,
                Biome = value.Biome.ToString().ToLowerInvariant(),
                Terrain = s.Wet ? "water" : "land",
                Water = new WaterState(s.Stream, false, s.FreshWater ? 0 : 10),
                ResourcePotential = s.Potentials,
                AssignedCityId = cityId,
                ParentNodeId = Macro(cell),
                TriangleIds = [$"{id}:a", $"{id}:b"],
                Diagonal = "nw-se",
                NaturalState = new NaturalState
                {
                    SoilQuality = value.Fertility,
                    ForestBiomass = value.ForestCover,
                    FishStock = s.Potentials["fish"],
                    Deposits = new Dictionary<string, double> { ["clay"] = s.Potentials["clay"] > 0 ? 1 : 0, ["stone"] = s.Potentials["stone"] > 0 ? 1 : 0, ["iron_ore"] = s.Potentials["iron_ore"] > 0 ? 1 : 0 },
                    ExtractedBatches = new()
                }
            };
        }
        foreach(var (cell,cityId) in claimed.OrderBy(pair=>ZoneId(pair.Key),StringComparer.Ordinal))
        {var id=ZoneId(cell);addresses[id]=cell;territories[id]=CreateTerritory(cell,cityId);}
        foreach (var assignment in economy.Cities)
            territories[ZoneId(FromAtlas(cities.Single(city => city.Id == assignment.SettlementId).Anchor))].Population = assignment.Population;
        var nodes = new Dictionary<string, SpatialNode>(StringComparer.Ordinal);
        foreach (var group in territories.Values.GroupBy(territory => territory.ParentNodeId))
            nodes[group.Key] = new SpatialNode { Id = group.Key, Kind = "macro", ParentNodeId = regionId, ChildTerritoryIds = group.Select(t => t.Id).ToArray(), Aggregate = SpatialGenerator.AggregateTerritories(group) };
        var macroIds = nodes.Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var city in cities)
        {
            var children = territories.Values.Where(territory => territory.AssignedCityId == city.Id).ToArray();
            var id = SpatialGenerator.CitySpatialNodeId(city.Id);
            nodes[id] = new SpatialNode
            {
                Id = id,
                Kind = "city",
                Projection = "settlement",
                WorldEntityId = city.Id,
                Name = city.Name,
                ParentNodeId = regionId,
                AnchorTerritoryId = ZoneId(FromAtlas(city.Anchor)),
                ChildTerritoryIds = children.Select(t => t.Id).ToArray(),
                Aggregate = SpatialGenerator.AggregateTerritories(children)
            };
        }
        nodes[regionId] = new SpatialNode
        {
            Id = regionId,
            Kind = "region",
            Name = economy.Name,
            ChildNodeIds = macroIds,
            OverlayNodeIds = cities.Select(city => SpatialGenerator.CitySpatialNodeId(city.Id)).ToArray(),
            Aggregate = SpatialGenerator.AggregateTerritories(territories.Values)
        };
        var grid = new SpatialGrid
        {
            Width = definition.FaceSize * 6,
            Height = definition.FaceSize,
            ZoneSizeMeters = definition.ZoneSizeMeters,
            AggregationFactor = definition.ChunkSize,
            MacroWidth = definition.FaceSize * 6 / definition.ChunkSize,
            MacroHeight = definition.FaceSize / definition.ChunkSize,
            VertexJitter = 0,
            Seed = definition.Seed,
            GeneratorVersion = 1,
            Levels = [new SpatialLevel(0, "spherical_cells", definition.FaceSize * 6, definition.FaceSize, 1)]
        };
        var actors = source.Scenario.ImportantActors.Select(actor =>
        {
            var sourceCity = source.Scenario.Cities.MinBy(city => Math.Pow(city.Anchor.X - actor.Zone.X, 2) + Math.Pow(city.Anchor.Y - actor.Zone.Y, 2))!;
            return actor with { Zone = cities.Single(city => city.Id == templatesToCities[sourceCity.Id]).Anchor };
        }).ToArray();
        foreach (var route in economy.Routes)
            if (route.A == route.B || !cities.Any(city => city.Id == route.A) || !cities.Any(city => city.Id == route.B) || route.TravelDays < 1 || route.DailyCapacity <= 0)
                throw new InvalidOperationException($"Некорректный маршрут {route.Id}");
        var scenario = source.Scenario with
        {
            Id = economy.Id,
            Name = economy.Name,
            Seed = definition.Seed,
            Cities = cities,
            Routes = economy.Routes,
            ImportantActors = actors,
            ScheduledEvents = []
        };
        var hydrologyIdentity = new { generator = SphericalHydrology.GeneratorVersion, hydro.Resolution, hydro.RunoffWeight };
        var fingerprint = CanonicalJson.Hash(JsonSerializer.SerializeToNode(new { source = source.Fingerprint, definition, economy, rules, hydrologyIdentity })!);
        var content = source with { Scenario = scenario, Fingerprint = fingerprint };
        var spatial = new SpatialHierarchy { RegionNodeId = regionId, Grid = grid, Nodes = nodes, Territories = territories };
        var world = snapshot is null ? WorldFactory.Create(content, spatial, coordinate => ZoneId(FromAtlas(coordinate)))
            : WorldSnapshot.Restore(content, snapshot);
        if(rules?.Primitive?.Biosphere is not null&&snapshot is not null)
        {
            if(world.Spatial.Territories.Count>100000)throw new InvalidOperationException("Превышен предел материализованных участков");
            foreach(var (id,t) in world.Spatial.Territories)
            {
                var cell=FromAtlas(new(){X=t.Grid.X,Y=t.Grid.Y});
                if(id!=ZoneId(cell)||cell.X<0||cell.Y<0||cell.X>=definition.FaceSize||cell.Y>=definition.FaceSize||(int)cell.Face is <0 or >5)
                    throw new InvalidOperationException("Некорректный материализованный участок");
                addresses.TryAdd(id,cell);
            }
        }
        if (!addresses.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(world.Spatial.Territories.Keys))
            throw new InvalidOperationException("Участки снимка не совпадают со сферическим сценарием");
        var simulation = new SphericalSimulation(content, world, settlements, sites, addresses, warnings);
        if (rules is not null)
        {
            var naturalRules = rules.NaturalPools.ToDictionary(p => p.Id);
            ScoutTerrain Survey(CellAddress cell)
            {
                var s = Sample(cell); var value = s.Value;
                double Renewal(string id, double potential) => naturalRules[id].Capacity * potential *
                    naturalRules[id].RecoveryPerDay * (rules.Subsistence?.RecoveryScale(id) ?? 1);
                var renewal = (s.Wet ? 0 : Renewal("forage", value.Fertility * (.4 + value.ForestCover * .6)) + Renewal("game", value.ForestCover)) +
                    Renewal("fish", s.Potentials["fish"]);
                return new(s.Wet, s.FreshWater, value.ElevationMeters, value.TemperatureC, value.Moisture, value.ForestCover, renewal);
            }
            Territory Materialize(CellAddress cell)
            {
                var id=ZoneId(cell);if(world.Spatial.Territories.TryGetValue(id,out var existing))return existing;
                if(world.Spatial.Territories.Count>=100000)throw new InvalidOperationException("Превышен бюджет материализации мира");
                var patch=CreateTerritory(cell,"");world.Spatial.Territories[id]=patch;addresses[id]=cell;
                if(world.Spatial.Nodes.TryGetValue(patch.ParentNodeId,out var macro))
                    world.Spatial.Nodes[macro.Id]=macro with {ChildTerritoryIds=macro.ChildTerritoryIds!.Append(id).ToArray()};
                else
                {
                    world.Spatial.Nodes[patch.ParentNodeId]=new(){Id=patch.ParentNodeId,Kind="macro",ParentNodeId=regionId,ChildTerritoryIds=[id],Aggregate=SpatialGenerator.AggregateTerritories([patch])};
                    var region=world.Spatial.Nodes[regionId];world.Spatial.Nodes[regionId]=region with{ChildNodeIds=region.ChildNodeIds!.Append(patch.ParentNodeId).ToArray()};
                }
                return patch;
            }
            simulation.Development = new SettlementSimulation(world, content, rules, topology, settlements, addresses, economy.Stage == "foragers", Survey, terrain, Materialize);
        }
        return simulation;
    }
    public static SphericalWorldDefinition PrepareWorld(SphericalWorldDefinition definition, SphericalEconomyDefinition economy) =>
        economy.Stage != "foragers" ? definition : definition with
        {
            Settlements = definition.Settlements.Select(city => city with
            {
                Buildings = [city.Buildings.First() with { Id = $"camp:{city.Id}", BuildingTypeId = "camp", InfluenceStrength = 1,
                Footprint = [city.Buildings.First().Footprint.First() with { CapacityUnits = 1 }] }],
                UsedLands = []
            }).ToArray()
        };

    private sealed record SiteTerrain(TerrainCellSample Value, bool Wet, bool NearWater, bool Stream, bool FreshWater, Dictionary<string, double> Potentials);
}

public sealed record SphericalIndustrySite(string IndustryId, string CityId, CellAddress Cell, string? LandId, string? BlockedReason);
