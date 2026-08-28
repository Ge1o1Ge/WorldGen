using System.Text.Json;
using WorldGen.Content;
using WorldGen.Core.Determinism;
using WorldGen.Core.Serialization;
using WorldGen.Core.Simulation;
using WorldGen.Core.Spatial;
using WorldGen.Core.Topology;
using WorldGen.Core.Settlements;

var canonicalOnly = args.Contains("--canonical", StringComparer.Ordinal);
var spatialSummaryOnly = args.Contains("--spatial-summary", StringComparer.Ordinal);
var worldCanonicalOnly = args.Contains("--world-canonical", StringComparer.Ordinal);
var sphereSummaryOnly = args.Contains("--sphere-summary", StringComparer.Ordinal);
var daysArgument = args.FirstOrDefault(argument => argument.StartsWith("--days=", StringComparison.Ordinal));
var simulationDays = daysArgument is null ? 0 : int.Parse(daysArgument[7..], System.Globalization.CultureInfo.InvariantCulture);
var positionalArgument = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
if (daysArgument is null && int.TryParse(positionalArgument, out var positionalDays)) simulationDays = positionalDays;
var contentDirectory = positionalArgument is not null && !int.TryParse(positionalArgument, out _) ? positionalArgument : null;
var content = await ContentLoader.LoadAsync(contentDirectory);

var replayArgument=args.FirstOrDefault(a=>a.StartsWith("--replay-check=",StringComparison.Ordinal));
if(replayArgument is not null)
{
    var saved=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(replayArgument[15..]))!;
    var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
    var definition=saved["topology"]!.Deserialize<SphericalWorldDefinition>(options)!;
    var economy=saved["economy"]!.Deserialize<SphericalEconomyDefinition>(options)!;
    var scenario=saved["world"]!["scenarioId"]!.GetValue<string>().Contains("primordial")?"primordial":"foragers";
    var rules=await SettlementRulesLoader.LoadAsync(contentDirectory,scenario);
    var topology=new CubeSphereTopology(definition.FaceSize);var generator=new SphericalTerrainGenerator(definition);var hydro=SphericalHydrology.Build(definition,generator);
    SphericalSimulation Restore(System.Text.Json.Nodes.JsonObject snapshot)=>SphericalSimulation.Create(content,definition,economy,topology,generator,hydro,
        SphericalSettlementLayer.Build(definition,topology,generator),rules,snapshot);
    var original=Restore(saved["world"]!.AsObject());original.Advance(7);
    var restored=Restore(WorldSnapshot.Create(original.World));
    if(WorldSnapshot.Hash(original.World)!=WorldSnapshot.Hash(restored.World))throw new InvalidOperationException("Снимок не восстановился тождественно");
    original.Advance(30);restored.Advance(30);
    var hash=WorldSnapshot.Hash(original.World);
    if(hash!=WorldSnapshot.Hash(restored.World))throw new InvalidOperationException("Продолжение после снимка разошлось");
    Console.WriteLine(JsonSerializer.Serialize(new{replay="passed",day=original.World.Day,hash}));return;
}

var hydroAuditArgument = args.FirstOrDefault(a => a.StartsWith("--hydrology-audit=", StringComparison.Ordinal));
if (hydroAuditArgument is not null)
{
    var snapshot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(hydroAuditArgument[18..]))!.AsObject();
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    var definition = snapshot["topology"]!.Deserialize<SphericalWorldDefinition>(options)!;
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var hydro = SphericalHydrology.Build(definition, new SphericalTerrainGenerator(definition));
    var buildings = snapshot["world"]!["settlementDevelopment"]!["buildings"]!.AsArray();
    var affected = new List<object>();
    foreach (var b in buildings)
    {
        var cell = b!["cell"]!.Deserialize<CellAddress>(options);
        var index = hydro.Index(cell);
        if (hydro.IsWater(index)) affected.Add(new { id=b["id"]!.GetValue<string>(), cell, depth=hydro.Surface[index]-hydro.Elevation[index] });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { hydro.Resolution, hydro.RunoffWeight, elapsedSeconds=timer.Elapsed.TotalSeconds,
        wetCells=hydro.Elevation.Where((_,i)=>hydro.IsLake(i)).Count(), affected }, options));
    return;
}

var sphereScenarioArgument = args.FirstOrDefault(a => a.StartsWith("--sphere-scenario=", StringComparison.Ordinal));
if (sphereScenarioArgument is not null)
{
    if (simulationDays < 0) throw new ArgumentOutOfRangeException(nameof(simulationDays));
    var scenario = sphereScenarioArgument[18..];
    var definition = await SphericalWorldLoader.LoadAsync(contentDirectory, scenario == "primordial" ? "spherical-primordial-world.json" : "cube-sphere-prototype.json");
    var seedArg=args.FirstOrDefault(a=>a.StartsWith("--seed=",StringComparison.Ordinal));
    if(seedArg is not null)definition=definition with{Seed=uint.Parse(seedArg[7..])};
    var economy = await SphericalEconomyLoader.LoadAsync(contentDirectory, scenario);
    var rules = await SettlementRulesLoader.LoadAsync(contentDirectory, scenario);
    definition = SphericalSimulation.PrepareWorld(definition, economy);
    var topology = new CubeSphereTopology(definition.FaceSize); var generator = new SphericalTerrainGenerator(definition);
    var hydro = SphericalHydrology.Build(definition, generator);
    if (scenario == "primordial") definition = PrimitiveStartLocations.Resolve(definition, generator, hydro);
    var settlementLayer=SphericalSettlementLayer.Build(definition,topology,generator);
    var simulation = SphericalSimulation.Create(content, definition, economy, topology, generator, hydro,settlementLayer, rules);
    var reports = new List<object>();
    var shortages = simulation.World.Cities.Keys.ToDictionary(id => id, _ => 0);
    var maximumLaborShare = 0d;
    for (var day = 0; day < simulationDays; day++)
    {
        simulation.Advance(1);
        foreach (var city in simulation.World.Cities.Values)
        {
            if (city.Shortage.Active) shortages[city.Id]++;
            var life = simulation.Development!.State.Cities[city.Id];
            if (life.LaborUsedHours > life.LaborAvailableHours + 1e-6 || city.Stocks.Values.Any(n => !double.IsFinite(n) || n < -1e-8))
                throw new InvalidOperationException($"Нарушение бюджета труда/запасов: {city.Id}, день {day}");
            maximumLaborShare = Math.Max(maximumLaborShare, life.LaborUsedHours / Math.Max(1e-9, life.LaborAvailableHours));
            if(life.Biology is {} biology)
            {
                if(biology.Plots.Values.Any(p=>!double.IsFinite(p.Area+p.Health+p.DegreeDays+p.HarvestRemaining)||p.Area is <0 or >1||p.Health is <0 or >1||p.HarvestRemaining<0)||
                    biology.Herds.Values.Any(h=>h.Count!=h.Captured+h.Births-h.Deaths-h.Slaughtered||h.Females<0||h.Males<0||h.Young.Any(y=>y.Count<0)))
                    throw new InvalidOperationException($"Нарушение биологического баланса: {city.Id}, день {day}");
            }
        }
        if (simulation.World.Day % 365 == 0 || day == simulationDays - 1)
        {
        if(args.Contains("--progress"))Console.Error.WriteLine($"День {simulation.World.Day}; население {simulation.World.Cities.Values.Sum(c=>simulation.World.Spatial.Nodes[c.SpatialNodeId].Aggregate.Population)}; участ {simulation.World.Spatial.Territories.Count}");
        reports.Add(JsonSerializer.SerializeToElement(new
        {
            simulation.World.Day,
            cities = simulation.World.Cities.Values.Select(c => new
            {
                c.Id, population = simulation.World.Spatial.Nodes[c.SpatialNodeId].Aggregate.Population,
                health = c.Demography.Health, freshFood = c.Stocks["food"], winterFood = c.Stocks.GetValueOrDefault("winter_food"),
                shortageDays = shortages[c.Id], life = JsonSerializer.SerializeToElement(simulation.Development!.State.Cities[c.Id].Primitive),
                known = simulation.Development.State.Cities[c.Id].Discoveries.Order(StringComparer.Ordinal).ToArray()
                ,biology=simulation.Development.State.Cities[c.Id].Biology,
                timber=c.Stocks["timber"],fuel=c.Stocks["firewood"],
                foodLabor=simulation.Development.State.Cities[c.Id].Food,
                fields=simulation.Development.State.Buildings.Where(b=>b.CityId==c.Id&&b.Kind=="garden").Select(b=>new{b.Id,b.Status,Soil=simulation.World.Spatial.Territories[SphericalSimulation.ZoneId(b.Cell)].NaturalState.SoilQuality}).ToArray()
            }).ToArray()
        }));
        }
    }
    var result=JsonSerializer.Serialize(new { scenario,seed=definition.Seed,day = simulation.World.Day, maximumLaborShare,
        stateHash = WorldSnapshot.Hash(simulation.World),activeZones=simulation.World.Spatial.Territories.Count,reports, fires = simulation.Development!.State.Atmosphere?.Ignitions });
    var reportArg=args.FirstOrDefault(a=>a.StartsWith("--report=",StringComparison.Ordinal));
    if(reportArg is not null){await File.WriteAllTextAsync(reportArg[9..],result);Console.WriteLine($"Отчёт: {reportArg[9..]}");}else Console.Write(result);
    var saveArg=args.FirstOrDefault(a=>a.StartsWith("--save=",StringComparison.Ordinal));
    if(saveArg is not null)await File.WriteAllTextAsync(saveArg[7..],JsonSerializer.Serialize(new {
        schemaVersion=2,hydrology=new{generator=SphericalHydrology.GeneratorVersion,hydro.Resolution,hydro.RunoffWeight},topology=definition,economy,
        settlementRules=rules,world=WorldSnapshot.Create(simulation.World),sites=simulation.Sites.Values,landUse=settlementLayer.UsedLands
    },new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
    return;
}

if (sphereSummaryOnly)
{
    var definition = await SphericalWorldLoader.LoadAsync(contentDirectory);
    var layout = new ChunkLayout(definition.FaceSize, definition.ChunkSize);
    var generator = new SphericalTerrainGenerator(definition);
    var samples = Enum.GetValues<CubeFace>().Select(face =>
    {
        var chunk = generator.GenerateChunk(new ChunkAddress(face, layout.ChunksPerFaceAxis / 2, layout.ChunksPerFaceAxis / 2));
        return new
        {
            face = face.ToString(),
            hash = chunk.ContentHash(),
            elevationMin = chunk.ElevationMeters.Min(),
            elevationMax = chunk.ElevationMeters.Max(),
            landCells = chunk.Biome.Count(biome => biome != SphericalBiome.Ocean)
        };
    });
    Console.Write(JsonSerializer.Serialize(new
    {
        definition.Id,
        definition.Name,
        definition.Topology,
        definition.FaceSize,
        definition.ChunkSize,
        definition.ZoneSizeMeters,
        zones = definition.ZoneCount,
        triangles = definition.TriangleCount,
        chunks = layout.ChunkCount,
        nominalAreaSquareKilometers = definition.NominalSurfaceAreaSquareKilometers,
        estimatedTerrainMiB = generator.EstimateResidentTerrainBytes() / 1024d / 1024d,
        samples
    }));
    return;
}

if (canonicalOnly)
{
    Console.Write(CanonicalJson.Serialize(content.Raw));
    return;
}

if (spatialSummaryOnly)
{
    var spatial = SpatialGenerator.Build(content);
    var summary = spatial.Territories.Values
        .GroupBy(territory => territory.AssignedCityId, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => new { territories = group.Count(), population = group.Sum(zone => zone.Population) },
            StringComparer.Ordinal);
    Console.Write(JsonSerializer.Serialize(summary));
    return;
}

if (worldCanonicalOnly)
{
    var canonicalWorld = WorldFactory.Create(content);
    SimulationEngine.Simulate(canonicalWorld, content, simulationDays);
    Console.Write(CanonicalJson.Serialize(WorldSnapshot.Create(canonicalWorld)));
    return;
}

var streams = SeededRandom.CreateStreams(content.Scenario.Seed,
    ["economy", "events", "institutions", "technology"]);
var world = WorldFactory.Create(content);
SimulationEngine.Simulate(world, content, simulationDays);

Console.WriteLine("WorldGen C# deterministic core");
Console.WriteLine($"Scenario: {content.Scenario.Name} [{content.Scenario.Id}]");
Console.WriteLine($"Content SHA-256: {content.Fingerprint}");
Console.WriteLine($"Day: {world.Day}");
Console.WriteLine($"World SHA-256: {WorldSnapshot.Hash(world)}");
Console.WriteLine($"Content: {content.Resources.Resources.Count} resources; " +
    $"{content.Recipes.Recipes.Count} recipes; {content.Technologies.Technologies.Count} technologies");
Console.WriteLine($"Map: {content.Map.Grid.Width}x{content.Map.Grid.Height}; generator v{content.Map.GeneratorVersion}");
Console.WriteLine($"World: {world.Spatial.Territories.Count} territories; {world.Cities.Count} cities; " +
    $"{world.Actors.Count} significant actors; population={world.Spatial.Nodes[world.Spatial.RegionNodeId].Aggregate.Population}");
Console.WriteLine("Deterministic streams:");
foreach (var stream in streams)
{
    Console.WriteLine($"- {stream.Key}: first={stream.Value.NextDouble():R}; state={stream.Value.State}");
}
