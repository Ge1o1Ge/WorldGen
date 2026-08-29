using Microsoft.Extensions.FileProviders;
using WorldGen.Content;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;
using WorldGen.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression();
var app = builder.Build();
app.UseResponseCompression();
var content = await ContentLoader.LoadAsync();
var sphereScenario = builder.Configuration["sphere-scenario"] ?? "primordial";
var sphericalDefinition = await SphericalWorldLoader.LoadAsync(fileName: sphereScenario == "primordial" ? "spherical-primordial-world.json" : "cube-sphere-prototype.json");
var sphericalEconomyDefinition = await SphericalEconomyLoader.LoadAsync(scenario: sphereScenario);
sphericalDefinition = SphericalSimulation.PrepareWorld(sphericalDefinition, sphericalEconomyDefinition);
var settlementRules = await SettlementRulesLoader.LoadAsync(scenario: sphereScenario);
var sphericalTopology = new CubeSphereTopology(sphericalDefinition.FaceSize);
var sphericalLayout = new ChunkLayout(sphericalDefinition.FaceSize, sphericalDefinition.ChunkSize);
var sphericalTerrain = new SphericalTerrainGenerator(sphericalDefinition);
var startupHydrology = sphereScenario == "primordial" ? SphericalHydrology.Build(sphericalDefinition, sphericalTerrain) : null;
if (startupHydrology is not null) sphericalDefinition = PrimitiveStartLocations.Resolve(sphericalDefinition, sphericalTerrain, startupHydrology);
var sphericalSettlements = SphericalSettlementLayer.Build(sphericalDefinition, sphericalTopology, sphericalTerrain);
var sphericalSettlementIndex = sphericalDefinition.Settlements
    .Select((settlement, index) => (settlement.Id, Index: index))
    .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
var spherePreviewCache = new System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<object>>();
var sphereLock = new object();
var sphericalHydrology = new Lazy<SphericalHydrology>(() => startupHydrology ?? SphericalHydrology.Build(sphericalDefinition, sphericalTerrain));
// Explicit startup restore lets a renderer/server update preserve the running world.
var sphereSnapshotPath = builder.Configuration["sphere-snapshot"];
var sphereSnapshot = sphereSnapshotPath is null ? null : System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(sphereSnapshotPath))!.AsObject();
if (sphereSnapshot is not null && (sphereSnapshot["schemaVersion"]?.GetValue<int>() != 2 || sphereSnapshot["world"] is not System.Text.Json.Nodes.JsonObject))
    throw new InvalidOperationException("Ожидается сферический снимок версии 2 с состоянием мира");
if (sphereSnapshot is not null && (sphereSnapshot["hydrology"]?["generator"]?.GetValue<string>() != SphericalHydrology.GeneratorVersion ||
    sphereSnapshot["hydrology"]?["resolution"]?.GetValue<int>() != sphericalHydrology.Value.Resolution ||
    sphereSnapshot["hydrology"]?["runoffWeight"]?.GetValue<float>() != sphericalHydrology.Value.RunoffWeight))
    throw new InvalidOperationException("Снимок использует другую гидрологию. Нужна явная миграция площадок или новый мир; старые постройки нельзя молча затопить");
var sphericalSimulation = SphericalSimulation.Create(content, sphericalDefinition, sphericalEconomyDefinition,
    sphericalTopology, sphericalTerrain, sphericalHydrology.Value, sphericalSettlements, settlementRules, sphereSnapshot?["world"]?.AsObject());
if (sphereSnapshot?["landUse"] is System.Text.Json.Nodes.JsonArray savedLands)
    foreach (var land in savedLands)
        if (!sphericalSettlements.SetLandUsage(land!["id"]!.GetValue<string>(), land["usage"]!.GetValue<float>()))
            throw new InvalidOperationException("Угодье снимка отсутствует в сценарии");
var sphereWorldId = Guid.NewGuid().ToString("N");
long SphereRevision() => ((long)sphericalSimulation.World.Day << 32) + sphericalSettlements.Revision;
var hydrologyView = new Lazy<object>(() =>
{
    var hydro = sphericalHydrology.Value;
    var faceLength = hydro.Resolution * hydro.Resolution;
    return new
    {
        generator = SphericalHydrology.GeneratorVersion,
        worldId = sphereWorldId,
        revision = 1,
        stride = sphericalDefinition.FaceSize / hydro.Resolution,
        hydro.Resolution,
        hydro.SeaLevel,
        minimumRunoff = SphericalHydrology.MinimumRiverRunoff,
        hydro.RunoffWeight,
        lakeDepthThreshold = SphericalHydrology.LakeDepthThreshold,
        reaches = hydro.BuildReaches().Select(reach => new
        {
            reach.Id,
            reach.Runoff,
            points = reach.Points.Select(point => new[] { point.X, point.Y, point.Z }).ToArray()
        }).ToArray(),
        faces = Enum.GetValues<CubeFace>().Select(face => new
        {
            face = face.ToString(),
            lakeDepth = Enumerable.Range((int)face * faceLength, faceLength)
                .Select(index => hydro.Elevation[index] > hydro.SeaLevel
                    ? Math.Max(0, hydro.Surface[index] - hydro.Elevation[index]) : 0).ToArray(),
            lakeShore = hydro.LakeShore.AsSpan((int)face * faceLength, faceLength).ToArray()
        }).ToArray()
    };
});
var projectRoot = Directory.GetParent(ContentLoader.FindContentDirectory())?.FullName ?? builder.Environment.ContentRootPath;
var visualizerDirectory = Path.Combine(projectRoot, "visualizer");
await app.MapTechnologyEditorAsync(projectRoot, content, settlementRules.Primitive);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    runtime = ".NET 10",
    scenarioId = sphericalSimulation.World.ScenarioId,
    contentFingerprint = sphericalSimulation.Content.Fingerprint,
    day = sphericalSimulation.World.Day,
    steppingAvailable = true
}));

app.MapGet("/", () => Results.Redirect("/sphere.html"));
app.MapGet("/index.html", () => Results.Redirect("/sphere.html"));
app.MapGet("/api/sphere/catalog", () => Results.Ok(new {
    biosphere=settlementRules.Primitive?.Biosphere,
    technologies=settlementRules.Primitive?.Technologies,
    resources=sphericalSimulation.Content.Resources.Resources,
    requirements=new { cropRotationHarvestedSpecies=settlementRules.Primitive?.Biosphere?.RotationCropCount }
}));

app.MapGet("/api/sphere", () =>
{
    lock (sphereLock) return Results.Ok(new
    {
        revision = SphereRevision(),
        worldId = sphereWorldId,
        sphericalDefinition.Id,
        sphericalDefinition.Name,
        sphericalDefinition.Topology,
        sphericalDefinition.FaceSize,
        sphericalDefinition.ChunkSize,
        sphericalDefinition.ZoneSizeMeters,
        sphericalDefinition.Seed,
        sphericalDefinition.Terrain.SeaLevelMeters,
        biosphere = settlementRules.Primitive?.Biosphere,
        processes = settlementRules.Primitive?.Processes,
        zones = sphericalTopology.CellCount,
        triangles = sphericalTopology.TriangleCount,
        chunks = sphericalLayout.ChunkCount,
        chunksPerFaceAxis = sphericalLayout.ChunksPerFaceAxis,
        nominalAreaSquareKilometers = sphericalDefinition.NominalSurfaceAreaSquareKilometers,
        estimatedTerrainMiB = sphericalTerrain.EstimateResidentTerrainBytes() / 1024d / 1024d,
        faces = Enum.GetNames<CubeFace>(),
        settlements = SphereSettlementViews(),
        influencedCells = sphericalSettlements.Influence.Cells.Count,
        boundaryCells = sphericalSettlements.Influence.BoundaryCells.Count()
    });
});

object[] SphereSettlementViews() => sphericalDefinition.Settlements.Select(settlement => new
{
    settlement.Id,
    settlement.Name,
    buildings = sphericalSettlements.Construction.Buildings.Values.Where(building => building.CityId == settlement.Id)
                .SelectMany(building => building.Footprint.Select(allocation => new
                {
                    building.Id,
                    building.BuildingTypeId,
                    status = sphericalSimulation.Development!.State.Buildings.FirstOrDefault(b => b.Id == building.Id)?.Status ?? "active",
                    residents = sphericalSimulation.Development!.State.Buildings.FirstOrDefault(b => b.Id == building.Id)?.Residents ?? 0,
                    slot = sphericalSimulation.Development!.State.Buildings.FirstOrDefault(b => b.Id == building.Id)?.Slot ?? -1,
                    face = allocation.Cell.Face.ToString(),
                    allocation.Cell.X,
                    allocation.Cell.Y,
                    allocation.CapacityUnits
                })).ToArray(),
    usedLands = sphericalSettlements.UsedLands.Where(land => land.CityId == settlement.Id).Select(land => new
    {
        land.Id,
        face = land.Cell.Face.ToString(),
        land.Cell.X,
        land.Cell.Y,
        kind = land.Kind.ToString(),
        land.Usage
    }).ToArray()
}).ToArray();

object SphereMapView(string? mapWorldId, long? mapClaimsRevision)
{
    var claimsRevision = sphericalSettlements.Revision;
    var sameClaims = mapWorldId == sphereWorldId && mapClaimsRevision == claimsRevision;
    return new
    {
        worldId = sphereWorldId,
        revision = SphereRevision(),
        claimsRevision,
        // Cumulative sparse overrides: skipped responses need no replay or terrain reload.
        forest = sphericalSimulation.World.Spatial.Territories.Values
            .Where(t => (float)t.NaturalState.ForestBiomass != (float)t.ForestCover)
            .Select(t =>
            {
                var cell = sphericalSimulation.Addresses[t.Id];
                return new { face = cell.Face.ToString(), cell.X, cell.Y, forest = (float)t.NaturalState.ForestBiomass };
            }).ToArray(),
        claims = sameClaims ? null : sphericalSettlements.Influence.Cells.Select(pair => new
        {
            face = pair.Key.Face.ToString(),
            pair.Key.X,
            pair.Key.Y,
            owner = sphericalSettlementIndex[pair.Value.CityId],
            influence = pair.Value.Strength
        }).ToArray(),
        settlements = sameClaims ? null : SphereSettlementViews()
    };
}

app.MapGet("/api/sphere/map", (string? mapWorldId, long? mapClaimsRevision) =>
{
    lock (sphereLock) return Results.Ok(SphereMapView(mapWorldId, mapClaimsRevision));
});

app.MapPost("/api/sphere/land-use/{id}", (string id, float usage) =>
{
    if (!float.IsFinite(usage) || usage is < 0 or > 1)
        return Results.BadRequest(new { error = "usage должен быть от 0 до 1" });
    lock (sphereLock)
    {
        if (!sphericalSettlements.SetLandUsage(id, usage))
            return Results.NotFound(new { error = "Угодье не найдено" });
        var revision = SphereRevision();
        return Results.Ok(new { revision, id, usage, influencedCells = sphericalSettlements.Influence.Cells.Count });
    }
});

app.MapGet("/api/sphere/chunks/{face}/{chunkX:int}/{chunkY:int}", (string face, int chunkX, int chunkY) =>
{
    if (!Enum.TryParse<CubeFace>(face, true, out var parsedFace) || !Enum.IsDefined(parsedFace))
        return Results.BadRequest(new { error = $"Неизвестная грань '{face}'" });
    var address = new ChunkAddress(parsedFace, chunkX, chunkY);
    if ((uint)chunkX >= sphericalLayout.ChunksPerFaceAxis || (uint)chunkY >= sphericalLayout.ChunksPerFaceAxis)
        return Results.BadRequest(new { error = "Координаты чанка выходят за пределы грани" });

    var chunk = sphericalTerrain.GenerateChunk(address);
    lock (sphereLock)
    {
        var owner = Enumerable.Repeat(-1, chunk.CellCount).ToArray();
        var influence = new float[chunk.CellCount];
        return Results.Ok(new
        {
            revision = 0,
            worldId = sphereWorldId,
            owner,
            influence,
            face = parsedFace.ToString(),
            chunkX,
            chunkY,
            originX = chunkX * sphericalDefinition.ChunkSize,
            originY = chunkY * sphericalDefinition.ChunkSize,
            chunk.Width,
            chunk.Height,
            hash = chunk.ContentHash(),
            elevationMeters = chunk.ElevationMeters,
            temperatureC = chunk.TemperatureC,
            moisture = chunk.Moisture,
            fertility = chunk.Fertility,
            forestCover = chunk.ForestCover,
            traversalCost = chunk.TraversalCost.Select(value => float.IsFinite(value) ? value : (float?)null),
            biome = chunk.Biome.Select(value => (int)value).ToArray()
        });
    }
});

app.MapGet("/api/sphere/preview", (int? stride) =>
{
    var sampleStride = stride ?? 4;
    if (sampleStride is not (2 or 4 or 8 or 16))
        return Results.BadRequest(new { error = "stride должен быть равен 2, 4, 8 или 16" });
    lock (sphereLock)
    {
        var preview = spherePreviewCache.GetOrAdd(sampleStride, value => new Lazy<object>(() =>
        {
            var resolution = (sphericalDefinition.FaceSize + value - 1) / value;
            var faces = Enum.GetValues<CubeFace>().Select(face =>
            {
                var length = resolution * resolution;
                var elevation = new float[length];
                var temperature = new float[length];
                var moisture = new float[length];
                var forest = new float[length];
                var biome = new int[length];
                var owner = Enumerable.Repeat(-1, length).ToArray();
                var influence = new float[length];
                for (var y = 0; y < resolution; y++)
                {
                    for (var x = 0; x < resolution; x++)
                    {
                        var cellX = Math.Min(sphericalDefinition.FaceSize - 1, x * value + value / 2);
                        var cellY = Math.Min(sphericalDefinition.FaceSize - 1, y * value + value / 2);
                        var cell = new CellAddress(face, cellX, cellY);
                        var sample = sphericalTerrain.GenerateCell(cell);
                        var index = y * resolution + x;
                        elevation[index] = sample.ElevationMeters;
                        temperature[index] = sample.TemperatureC;
                        moisture[index] = sample.Moisture;
                        forest[index] = sample.ForestCover;
                        biome[index] = (int)sample.Biome;
                    }
                }
                return new { face = face.ToString(), elevation, temperature, moisture, forest, biome, owner, influence };
            }).ToArray();
            return new { revision = 0, worldId = sphereWorldId, stride = value, resolution, faceSize = sphericalDefinition.FaceSize, faces };
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return Results.Ok(preview);
    }
});

app.MapGet("/api/sphere/hydrology", () => Results.Ok(hydrologyView.Value));

object SphereSimulationView(string? mapWorldId = null, long? mapClaimsRevision = null)
{
    var simulation = sphericalSimulation.World;
    return new
    {
        revision = SphereRevision(),
        simulation.Day,
        simulation.ScenarioId,
        map = SphereMapView(mapWorldId, mapClaimsRevision),
        name = sphericalEconomyDefinition.Name,
        activeZones = simulation.Spatial.Territories.Count,
        weatherMap = sphericalSimulation.Development?.WeatherMap(),
        biologyPlots = sphericalSimulation.Development!.State.Buildings.Where(b=>b.Kind=="garden"&&b.Status=="active")
            .Select(b=>new{building=b,plot=sphericalSimulation.Development.State.Cities[b.CityId].Biology?.Plots.GetValueOrDefault(b.Id)})
            .Where(p=>p.plot?.CropId is not null).Select(p=>new {p.building.Id,p.building.CityId,face=p.building.Cell.Face.ToString(),p.building.Cell.X,p.building.Cell.Y,p.plot!.CropId,p.plot.Area,p.plot.Phase}).ToArray(),
        resourceCamps = sphericalSimulation.Development.State.Cities.SelectMany(c=>c.Value.Biology?.Camps.Select(p=>new {cityId=c.Key,p.Id,face=p.Cell.Face.ToString(),p.Cell.X,p.Cell.Y,p.Work,p.Abandoned,p.Delivered})??[]).ToArray(),
        residentsPerHouse = settlementRules.ResidentsPerHouse,
        scenarioStage = sphericalEconomyDefinition.Stage,
        activityNames = settlementRules.Activities.ToDictionary(a => a.Id, a => a.Name),
        discoveryNames = settlementRules.Discoveries.ToDictionary(a => a.Id, a => a.Name),
        resourceUnits = sphericalSimulation.Content.Resources.Resources.ToDictionary(r => r.Id, r => r.Unit),
        atmosphere = sphericalSimulation.Development!.State.Atmosphere is { } atmosphere ? new
        {
            atmosphere.LastDay, systems = atmosphere.Systems.Count, atmosphere.Ignitions, atmosphere.BurnedTimber,
            burningCells = atmosphere.Ground.Count(g => g.Value.Fire > 0)
        } : null,
        naturalPools = settlementRules.NaturalPools,
        explorationRules = settlementRules.Exploration,
        wildlife = (sphericalSimulation.Development!.State.Wildlife ?? []).Select(group => new
        {
            group.Id,
            group.SpeciesId,
            group.Biomass,
            group.Capacity,
            group.Alert,
            group.RadiusCells,
            group.LastMoveDay,
            group.LastHuntedDay,
            group.Moves,
            group.Harvested,
            group.Regrown,
            face = group.Center.Face.ToString(),
            group.Center.X,
            group.Center.Y,
            previous = new { face = group.PreviousCenter.Face.ToString(), group.PreviousCenter.X, group.PreviousCenter.Y }
        }).ToArray(),
        scouts = (sphericalSimulation.Development!.State.Scouting?.Expeditions ?? []).Select(e => new
        {
            e.Id,
            e.CityId,
            e.People,
            e.Phase,
            e.DepartureDay,
            e.ReturnDay,
            e.LastStepDay,
            face = e.Current.Face.ToString(),
            e.Current.X,
            e.Current.Y,
            traversedCells = e.Path.Count - 1,
            e.Food,
            e.Water
        }).ToArray(),
        trailSummary = new
        {
            total = sphericalSimulation.Development!.State.Trails.Count,
            visible = sphericalSimulation.Development.State.Trails.Count(edge => edge.Strength > .025),
            usedToday = sphericalSimulation.Development.State.Trails.Count(edge => edge.Passages > 0)
        },
        trails = sphericalSimulation.Development!.State.Trails.Where(edge => edge.Strength > .025).Select(edge => new
        {
            from = new { face = edge.From.Face.ToString(), edge.From.X, edge.From.Y },
            to = new { face = edge.To.Face.ToString(), edge.To.X, edge.To.Y },
            edge.Strength,
            edge.Passages
        }).ToArray(),
        warnings = sphericalSimulation.Warnings,
        cities = simulation.Cities.Values.Select(city => new
        {
            city.Id,
            city.Name,
            biology = sphericalSimulation.Development.State.Cities[city.Id].Biology,
            activeCropPlots = sphericalSimulation.Development.State.Buildings.Where(b=>b.CityId==city.Id&&b.Kind=="garden"&&b.Status=="active").Select(b=>b.Id).ToArray(),
            population = simulation.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population,
            stocks = city.Stocks.ToDictionary(),
            health = city.Demography.Health,
            foodDays = city.Stocks["food"] / Math.Max(.001, simulation.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population * city.FoodPerPersonPerDay),
            shortage = city.Shortage.Active,
            worldKnowledge = new
            {
                settlements = (city.KnowledgeState.KnownSettlements?.Values.ToArray() ?? []).Select(place => new
                {
                    place.CityId,
                    place.Name,
                    face = place.Cell.Face.ToString(),
                    place.Cell.X,
                    place.Cell.Y,
                    place.ObservedDay,
                    place.ReceivedDay,
                    place.SourceCityId,
                    place.Channel,
                    place.Confidence
                }).ToArray(),
                observationCount = city.KnowledgeState.Observations.Count,
                recent = simulation.Journal.Where(evt => city.KnowledgeState.Observations.ContainsKey(evt.Id)).TakeLast(4).Reverse().Select(evt => new
                {
                    evt.Type,
                    evt.Day,
                    city.KnowledgeState.Observations[evt.Id].ReceivedDay,
                    city.KnowledgeState.Observations[evt.Id].Channel,
                    city.KnowledgeState.Observations[evt.Id].Confidence
                }).ToArray()
            },
            technologyCount = city.TechnologyState.Count,
            council = sphericalSimulation.Development!.State.Cities[city.Id].Council is { } council ? new
            {
                council.LastDay,
                council.IssuedToday,
                council.SpentToday,
                council.WeightedToday,
                council.DelegatedToday,
                households = council.Profiles.Count(p => p.Value.Members > 0),
                reputation = new[] { "construction", "water", "food" }.ToDictionary(domain => domain, domain => new
                {
                    minimum = council.Profiles.Values.Where(p => p.Members > 0).Select(p => p.Reputation.GetValueOrDefault(domain, 1)).DefaultIfEmpty(1).Min(),
                    maximum = council.Profiles.Values.Where(p => p.Members > 0).Select(p => p.Reputation.GetValueOrDefault(domain, 1)).DefaultIfEmpty(1).Max()
                }),
                proposals = council.Proposals.Select(p => new
                {
                    p.Id,
                    p.Kind,
                    p.Reason,
                    p.Domain,
                    p.Phase,
                    p.CreatedDay,
                    p.Support,
                    p.RequiredSupport,
                    p.RequiredSiteSupport,
                    p.LeadingDays,
                    p.SelectedSite,
                    p.ApprovedDay,
                    p.BuildingId,
                    p.FinishedDay,
                    p.ObservedDays,
                    p.AssessedDay,
                    p.Outcome,
                    p.IdeaOutcome,
                    p.SiteOutcome,
                    p.OutcomeNote,
                    supporters = p.Backers.Select(b => b.SourceId).Distinct().Count(),
                    sites = p.Sites.Select(s => new { s.Id, s.Available, s.Support, face = s.Cell.Face.ToString(), s.Cell.X, s.Cell.Y }).ToArray()
                }).ToArray()
            } : null,
            scoutReports = (sphericalSimulation.Development!.State.Cities[city.Id].Supply?.Reports ?? []).Select(report => new
            {
                report.ExpeditionId,
                report.DepartureDay,
                report.ReceivedDay,
                report.SurveyedCells,
                report.Outcome,
                candidates = report.Candidates.Select(c => new
                {
                    face = c.Cell.Face.ToString(),
                    c.Cell.X,
                    c.Cell.Y,
                    c.ObservedDay,
                    c.FreshWater,
                    c.FoodRenewalPerDay
                }).ToArray()
            }).ToArray(),
            adoption = city.TechnologyState.Values.Average(technology => technology.Adoption),
            settlement = System.Text.Json.JsonSerializer.SerializeToElement(sphericalSimulation.Development!.State.Cities[city.Id],
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }),
            homes = sphericalSimulation.Development.State.Buildings.Where(b => b.CityId == city.Id && b.Status != "demolished").Select(b => new
            {
                b.Id,
                b.Kind,
                b.Status,
                b.Residents,
                householdId = sphericalSimulation.Development.HouseholdIdentity(b.Id),
                b.Slot,
                b.LaborDone,
                b.UnusedDays,
                b.ReadyDay,
                b.Replaces,
                b.MoveFinished,
                b.Lifecycle,
                b.Well,
                b.Field,
                soilQuality = simulation.Spatial.Territories[SphericalSimulation.ZoneId(b.Cell)].NaturalState.SoilQuality,
                face = b.Cell.Face.ToString(),
                b.Cell.X,
                b.Cell.Y
            }).ToArray(),
            industries = city.Industries.Select(industry =>
            {
                var site = sphericalSimulation.Sites[industry.Id];
                var natural = simulation.Spatial.Territories[industry.ZoneId].NaturalState;
                return new
                {
                    industry.Id,
                    industry.RecipeId,
                    name = content.Recipes.Recipes.Single(recipe => recipe.Id == industry.RecipeId).Name,
                    industry.TotalBatches,
                    industry.Capacity,
                    industry.LastConstraintKey,
                    face = site.Cell.Face.ToString(),
                    site.Cell.X,
                    site.Cell.Y,
                    site.LandId,
                    site.BlockedReason,
                    natural.ForestBiomass,
                    natural.SoilQuality
                };
            }).ToArray()
        }).ToArray(),
        lifecycleRules = settlementRules.Lifecycle,
        wellbeingRules = settlementRules.Wellbeing,
        lastDay = simulation.Telemetry.Daily.LastOrDefault(),
        shipments = simulation.Shipments.Count,
        events = simulation.Journal.TakeLast(12).Reverse().ToArray()
    };
}
app.MapGet("/api/sphere/simulation", (string? mapWorldId, long? mapClaimsRevision) => { lock (sphereLock) return Results.Ok(SphereSimulationView(mapWorldId, mapClaimsRevision)); });
app.MapPost("/api/sphere/step", (int? days, string? mapWorldId, long? mapClaimsRevision) =>
{
    var count = days ?? 1;
    if (count is < 1 or > 365) return Results.BadRequest(new { error = "days должно быть от 1 до 365" });
    lock (sphereLock)
    {
        sphericalSimulation.Advance(count);
        return Results.Ok(SphereSimulationView(mapWorldId, mapClaimsRevision));
    }
});
app.MapGet("/api/sphere/simulation/snapshot", () =>
{
    lock (sphereLock) return Results.Json(new
    {
        schemaVersion = 2,
        hydrology = new { generator = SphericalHydrology.GeneratorVersion, sphericalHydrology.Value.Resolution, sphericalHydrology.Value.RunoffWeight },
        topology = sphericalDefinition,
        economy = sphericalEconomyDefinition,
        settlementRules,
        world = WorldSnapshot.Create(sphericalSimulation.World),
        sites = sphericalSimulation.Sites.Values.ToArray(),
        landUse = sphericalSettlements.UsedLands.ToArray()
    });
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(visualizerDirectory),
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.Run();
