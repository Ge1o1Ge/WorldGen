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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
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
var sphereGpuSurface = new Lazy<byte[]>(BuildGpuSurface, LazyThreadSafetyMode.ExecutionAndPublication);
var sphereLock = new object();
// Only one browser tab may own the simulation clock. Other tabs may still
// observe and inspect the same world, but cannot accidentally advance it too.
var sphereLiveRunner = new SemaphoreSlim(1, 1);
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
        activities = settlementRules.Activities,
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

app.MapGet("/api/sphere/gpu-surface", (string? worldId) =>
{
    if (!StringComparer.Ordinal.Equals(worldId, sphereWorldId))
        return Results.Conflict(new { error = "Запрошена поверхность другого мира" });
    return Results.File(sphereGpuSurface.Value, "application/octet-stream");
});

byte[] BuildGpuSurface()
{
    var size = sphericalDefinition.FaceSize;
    var faceLength = checked(size * size);
    var pixels = new byte[checked(faceLength * 6 * 4)];
    foreach (var face in Enum.GetValues<CubeFace>())
    {
        for (var chunkY = 0; chunkY < sphericalLayout.ChunksPerFaceAxis; chunkY++)
        {
            for (var chunkX = 0; chunkX < sphericalLayout.ChunksPerFaceAxis; chunkX++)
            {
                var chunk = sphericalTerrain.GenerateChunk(new ChunkAddress(face, chunkX, chunkY));
                var originX = chunkX * sphericalDefinition.ChunkSize;
                var originY = chunkY * sphericalDefinition.ChunkSize;
                for (var localY = 0; localY < chunk.Height; localY++)
                {
                    for (var localX = 0; localX < chunk.Width; localX++)
                    {
                        var source = chunk.Index(localX, localY);
                        var target = (((int)face * faceLength) + (originY + localY) * size + originX + localX) * 4;
                        var forest = Math.Clamp((chunk.ForestCover[source] - .3f) / .25f, 0, 1);
                        var r = 244f - 40f * forest;
                        var g = 240f - 20f * forest;
                        var b = 223f - 36f * forest;
                        if (chunk.ElevationMeters[source] <= sphericalDefinition.Terrain.SeaLevelMeters)
                        {
                            var amount = (float)Math.Clamp((sphericalDefinition.Terrain.SeaLevelMeters - chunk.ElevationMeters[source]) / 240f, 0, 1);
                            r = 195 - 42 * amount; g = 220 - 26 * amount; b = 226 - 17 * amount;
                        }
                        else if (chunk.Biome[source] == SphericalBiome.Tundra)
                        { r += (229 - r) * .55f; g += (231 - g) * .55f; b += (224 - b) * .55f; }
                        else if (chunk.Biome[source] == SphericalBiome.DryGrassland)
                        { r += (238 - r) * .3f; g += (223 - g) * .3f; b += (184 - b) * .3f; }
                        else if (chunk.Biome[source] == SphericalBiome.Wetland)
                        { r += (200 - r) * .45f; g += (220 - g) * .45f; b += (211 - b) * .45f; }
                        pixels[target] = (byte)Math.Clamp((int)r, 0, 255);
                        pixels[target + 1] = (byte)Math.Clamp((int)g, 0, 255);
                        pixels[target + 2] = (byte)Math.Clamp((int)b, 0, 255);
                        pixels[target + 3] = 255;
                    }
                }
            }
        }
    }
    return pixels;
}

app.MapGet("/api/sphere/hydrology", () => Results.Ok(hydrologyView.Value));

double ScoutCargoUsed(ScoutExpedition expedition) => expedition.CargoUsed + expedition.CapturedAnimals.Sum(pair => pair.Value *
    (settlementRules.Primitive?.Biosphere?.Animals.FirstOrDefault(animal => animal.Id == pair.Key)?.BodyTonnes ?? 0));

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
        resourceNames = sphericalSimulation.Content.Resources.Resources.ToDictionary(r => r.Id, r => r.Name),
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
            e.InitialPeople,
            e.Phase,
            e.DepartureDay,
            e.ReturnDay,
            e.LostDay,
            e.LastStepDay,
            face = e.Current.Face.ToString(),
            e.Current.X,
            e.Current.Y,
            routeIndex = e.Phase == "outbound" ? e.Path.Count - 1 : e.ReturnIndex,
            path = e.Path.Select(cell => new { face = cell.Face.ToString(), cell.X, cell.Y }).ToArray(),
            traversedCells = e.Path.Count - 1,
            e.ProvisionDays,
            e.PlannedOutboundDays,
            e.ExtensionDays,
            e.CargoCapacity,
            cargoUsed = ScoutCargoUsed(e),
            e.TravelMode,
            e.CurrentInterest,
            e.SpeedMultiplier,
            e.Food,
            e.Water,
            e.ForagedFood,
            e.RefilledWater,
            e.Casualties,
            capturedAnimals = e.CapturedAnimals
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
            technology = new
            {
                total = city.TechnologyState.Count,
                known = city.TechnologyState.Values.Count(value => value.Knowledge > .01),
                competent = city.TechnologyState.Values.Count(value => value.Competence > .01),
                capable = city.TechnologyState.Values.Count(value => value.Capability > .01),
                adopted = city.TechnologyState.Values.Count(value => value.Adoption > .01)
            },
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
                report.Plants,
                report.Animals,
                report.CapturedAnimals,
                report.Casualties,
                report.ForeignClaims,
                candidates = report.Candidates.Select(c => new
                {
                    face = c.Cell.Face.ToString(),
                    c.Cell.X,
                    c.Cell.Y,
                    c.ObservedDay,
                    c.FreshWater,
                    c.FoodRenewalPerDay,
                    c.Plants,
                    c.Animals
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
        // The client performs scale- and time-aware decluttering. Keep a bounded
        // history window here so the journal can change scope without another request.
        events = simulation.Journal.TakeLast(240).Reverse().ToArray()
    };
}
app.MapGet("/api/sphere/simulation", (string? mapWorldId, long? mapClaimsRevision) => { lock (sphereLock) return Results.Ok(SphereSimulationView(mapWorldId, mapClaimsRevision)); });
app.MapPost("/api/sphere/step", (int? days, string? mapWorldId, long? mapClaimsRevision) =>
{
    var count = days ?? 1;
    if (count is < 1 or > 365) return Results.BadRequest(new { error = "days должно быть от 1 до 365" });
    if (!sphereLiveRunner.Wait(0)) return Results.Conflict(new { error = "Симуляцией управляет открытый WebSocket" });
    try
    {
        lock (sphereLock)
        {
            sphericalSimulation.Advance(count);
            return Results.Ok(SphereSimulationView(mapWorldId, mapClaimsRevision));
        }
    }
    finally { sphereLiveRunner.Release(); }
});
app.MapPost("/api/sphere/run", (int? speed, int? cycles, string? mapWorldId, long? mapClaimsRevision, CancellationToken requestAborted) =>
{
    var multiplier = speed ?? 1;
    var batchCycles = cycles ?? 10;
    if (multiplier is not (1 or 7 or 30)) return Results.BadRequest(new { error = "speed должен быть 1, 7 или 30" });
    if (batchCycles is < 1 or > 10) return Results.BadRequest(new { error = "cycles должно быть от 1 до 10" });
    if (!sphereLiveRunner.Wait(0)) return Results.Conflict(new { error = "Симуляцией управляет открытый WebSocket" });
    try
    {
        lock (sphereLock)
        {
            var completed = 0;
            for (; completed < batchCycles; completed++)
            {
                requestAborted.ThrowIfCancellationRequested();
                sphericalSimulation.Advance(multiplier, requestAborted);
            }
            return Results.Ok(new { completedCycles = completed, advancedDays = completed * multiplier,
                state = SphereSimulationView(mapWorldId, mapClaimsRevision) });
        }
    }
    catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
    {
        // The tab disappeared. No background runner survives this bounded request.
        return Results.StatusCode(499);
    }
    finally { sphereLiveRunner.Release(); }
});
object SphereLiveView(long previousClaimsRevision, bool includeMap)
{
    var simulation = sphericalSimulation.World;
    return new
    {
        type = "state",
        revision = SphereRevision(),
        simulation.Day,
        // Structural map data is pushed only when buildings, fields or claims
        // actually changed. Natural terrain remains a paused full-sync concern.
        map = includeMap ? SphereMapView(sphereWorldId, previousClaimsRevision) : null,
        weatherMap = sphericalSimulation.Development?.WeatherMap(),
        atmosphere = sphericalSimulation.Development!.State.Atmosphere is { } atmosphere ? new
        {
            atmosphere.LastDay, systems = atmosphere.Systems.Count, atmosphere.Ignitions, atmosphere.BurnedTimber,
            burningCells = atmosphere.Ground.Count(g => g.Value.Fire > 0)
        } : null,
        scouts = (sphericalSimulation.Development.State.Scouting?.Expeditions ?? []).Select(e => new
        {
            e.Id, e.CityId, e.People, e.InitialPeople, e.Phase, e.TravelMode, e.CurrentInterest,
            e.DepartureDay, e.ReturnDay, e.LostDay, e.LastStepDay,
            face = e.Current.Face.ToString(), e.Current.X, e.Current.Y,
            routeIndex = e.Phase == "outbound" ? e.Path.Count - 1 : e.ReturnIndex,
            path = e.Path.Select(cell => new { face = cell.Face.ToString(), cell.X, cell.Y }).ToArray(),
            traversedCells = e.Path.Count - 1, e.ProvisionDays, e.PlannedOutboundDays, e.ExtensionDays,
            e.CargoCapacity, cargoUsed = ScoutCargoUsed(e), e.SpeedMultiplier, e.Food, e.Water,
            e.ForagedFood, e.RefilledWater, e.Casualties, capturedAnimals = e.CapturedAnimals
        }).ToArray(),
        cities = simulation.Cities.Values.Select(city =>
        {
            var life = sphericalSimulation.Development.State.Cities[city.Id];
            var population = simulation.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population;
            return new
            {
                city.Id,
                population,
                stocks = city.Stocks.ToDictionary(),
                health = city.Demography.Health,
                foodDays = city.Stocks["food"] / Math.Max(.001, population * city.FoodPerPersonPerDay),
                shortage = city.Shortage.Active,
                technology = new
                {
                    total = city.TechnologyState.Count,
                    known = city.TechnologyState.Values.Count(value => value.Knowledge > .01),
                    competent = city.TechnologyState.Values.Count(value => value.Competence > .01),
                    capable = city.TechnologyState.Values.Count(value => value.Capability > .01),
                    adopted = city.TechnologyState.Values.Count(value => value.Adoption > .01)
                },
                settlement = new
                {
                    life.HousingCapacity, life.Unhoused, life.WaterCoverage, life.LaborAvailableHours,
                    life.LaborUsedHours, life.IndustryLaborHours, life.WaterTravelHours,
                    life.Tasks,
                    wellbeing = life.Wellbeing is null ? null : new { life.Wellbeing.Satisfaction }
                },
                homes = sphericalSimulation.Development.State.Buildings.Where(building => building.CityId == city.Id && building.Status != "demolished")
                    .Select(building => new { building.Id, building.Status, building.Residents }).ToArray()
            };
        }).ToArray(),
        events = simulation.Journal.TakeLast(48).Reverse().ToArray()
    };
}

app.Map("/api/sphere/live", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var stopped = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var commands = System.Threading.Channels.Channel.CreateUnbounded<(string Type, int Speed)>();
    async Task ReceiveCommands()
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open && !stopped.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                System.Net.WebSockets.WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, stopped.Token);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) { stopped.Cancel(); return; }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != System.Net.WebSockets.WebSocketMessageType.Text) continue;
                using var json = System.Text.Json.JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var kind) ? kind.GetString() ?? "" : "";
                var speed = root.TryGetProperty("speed", out var value) ? value.GetInt32() : 1;
                if (type is "run" or "pause" or "ack" && (type != "run" || speed is 1 or 7 or 30))
                    await commands.Writer.WriteAsync((type, speed), stopped.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (System.Net.WebSockets.WebSocketException) { stopped.Cancel(); }
        finally { commands.Writer.TryComplete(); }
    }
    async Task Send(object value)
    {
        // Match ASP.NET's HTTP JSON contract. Anonymous projections contain
        // inferred PascalCase members such as Day and HousingCapacity.
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await socket.SendAsync(json, System.Net.WebSockets.WebSocketMessageType.Text, true, stopped.Token);
    }
    var receiver = ReceiveCommands();
    var playing = false; var ownsRunner = false; var speed = 1;
    var nextAdvanceAt = DateTimeOffset.MaxValue;
    var lastClaimsRevision = sphericalSettlements.Revision;
    var lastMapDay = sphericalSimulation.World.Day;
    async Task<bool> TryStart(int requestedSpeed)
    {
        if (!ownsRunner)
        {
            ownsRunner = await sphereLiveRunner.WaitAsync(0, stopped.Token);
            if (!ownsRunner)
            {
                await Send(new { type = "busy", day = sphericalSimulation.World.Day,
                    message = "Симуляцией уже управляет другая вкладка" });
                return false;
            }
        }
        var wasPlaying = playing;
        speed = requestedSpeed;
        playing = true;
        if (!wasPlaying) nextAdvanceAt = DateTimeOffset.UtcNow.AddSeconds(5);
        return true;
    }
    void Pause()
    {
        playing = false;
        if (!ownsRunner) return;
        ownsRunner = false;
        sphereLiveRunner.Release();
    }
    async Task WaitForNextAdvance()
    {
        while (playing && DateTimeOffset.UtcNow < nextAdvanceAt)
        {
            var remaining = nextAdvanceAt - DateTimeOffset.UtcNow;
            await Task.Delay(remaining > TimeSpan.FromMilliseconds(100) ? 100 : Math.Max(1, (int)remaining.TotalMilliseconds), stopped.Token);
            while (commands.Reader.TryRead(out var command))
            {
                if (command.Type == "pause") Pause();
                else if (command.Type == "run") await TryStart(command.Speed);
                // An acknowledgement belongs to an already completed frame.
            }
        }
    }
    try
    {
        await Send(new { type = "ready", day = sphericalSimulation.World.Day });
        while (!stopped.IsCancellationRequested && socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            if (!playing)
            {
                var command = await commands.Reader.ReadAsync(stopped.Token);
                if (command.Type == "run") await TryStart(command.Speed);
                continue;
            }
            while (commands.Reader.TryRead(out var command))
            {
                if (command.Type == "pause") Pause();
                else if (command.Type == "run") await TryStart(command.Speed);
            }
            if (!playing)
            {
                await Send(new { type = "paused", day = sphericalSimulation.World.Day });
                continue;
            }
            await WaitForNextAdvance();
            if (!playing)
            {
                await Send(new { type = "paused", day = sphericalSimulation.World.Day });
                continue;
            }
            object update;
            lock (sphereLock)
            {
                sphericalSimulation.Advance(speed, stopped.Token);
                var currentClaimsRevision = sphericalSettlements.Revision;
                var includeMap = currentClaimsRevision != lastClaimsRevision || sphericalSimulation.World.Day - lastMapDay >= 30;
                update = SphereLiveView(lastClaimsRevision, includeMap);
                lastClaimsRevision = sphericalSettlements.Revision;
                if (includeMap) lastMapDay = sphericalSimulation.World.Day;
            }
            await Send(update);
            // Explicit client acknowledgement provides backpressure. The server
            // never builds an unbounded queue while rendering is busy.
            var acknowledged = false;
            while (!acknowledged && playing && !stopped.IsCancellationRequested)
            {
                var command = await commands.Reader.ReadAsync(stopped.Token);
                if (command.Type == "ack") acknowledged = true;
                else if (command.Type == "pause") Pause();
                else if (command.Type == "run") await TryStart(command.Speed);
            }
            if (!playing) await Send(new { type = "paused", day = sphericalSimulation.World.Day });
            else nextAdvanceAt = DateTimeOffset.UtcNow.AddSeconds(5);
        }
    }
    catch (OperationCanceledException) { }
    catch (System.Net.WebSockets.WebSocketException) { }
    finally
    {
        Pause();
        stopped.Cancel();
        try { await receiver; } catch (OperationCanceledException) { }
        if (socket.State == System.Net.WebSockets.WebSocketState.Open)
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "stopped", CancellationToken.None);
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
