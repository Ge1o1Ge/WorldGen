using System.Text.Json;
using WorldGen.Content;
using WorldGen.Core.Determinism;
using WorldGen.Core.Simulation;
using WorldGen.Core.Spatial;

namespace WorldGen.Tests;

public sealed class MigrationParityTests
{
    [Fact]
    public async Task ContentFingerprintMatchesJavaScriptGoldenMaster()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();

        Assert.Equal(fixture.RootElement.GetProperty("contentHash").GetString(), content.Fingerprint);
        Assert.Equal(12, content.Resources.Resources.Count);
        Assert.Equal(15, content.Recipes.Recipes.Count);
        Assert.Equal(15, content.Technologies.Technologies.Count);
        Assert.Equal(10_000, content.Map.Grid.Width * content.Map.Grid.Height);
    }

    [Theory]
    [InlineData("economy")]
    [InlineData("events")]
    [InlineData("institutions")]
    [InlineData("technology")]
    public async Task RandomStreamMatchesJavaScriptExactly(string streamName)
    {
        using var fixture = await ReadFixtureAsync();
        var root = fixture.RootElement;
        var randomFixture = root.GetProperty("randomStreams").GetProperty(streamName);
        var random = new SeededRandom(root.GetProperty("seed").GetUInt32(), streamName);

        foreach (var expected in randomFixture.GetProperty("values").EnumerateArray())
        {
            Assert.Equal(expected.GetDouble(), random.NextDouble());
        }
        Assert.Equal(randomFixture.GetProperty("finalState").GetUInt32(), random.State);
    }

    [Fact]
    public async Task RequiredTechnologyGraphRejectsCycles()
    {
        var content = await ContentLoader.LoadAsync();
        var relations = content.Technologies.Relations.ToList();
        relations.Add(new()
        {
            From = "water_mill",
            To = "woodworking",
            Type = "required"
        });
        var changed = content with
        {
            Technologies = content.Technologies with { Relations = relations }
        };

        var exception = Assert.Throws<ContentValidationException>(() => ContentValidator.Validate(changed));
        Assert.Contains("цикл required-связей", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpatialHierarchyMatchesJavaScriptReferenceTerritories()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var spatial = SpatialGenerator.Build(content);

        Assert.Equal(10_000, spatial.Territories.Count);
        Assert.Equal(100, spatial.Nodes.Values.Count(node => node.Kind == "macro"));
        Assert.Equal(content.Map.Population.Total, spatial.Territories.Values.Sum(territory => territory.Population));
        Assert.Equal(content.Map.Population.Total, spatial.Nodes[spatial.RegionNodeId].Aggregate.Population);

        foreach (var property in fixture.RootElement.GetProperty("initialTerritories").EnumerateObject())
        {
            var expected = property.Value;
            var actual = spatial.Territories[property.Name];
            Assert.Equal(expected.GetProperty("assignedCityId").GetString(), actual.AssignedCityId);
            Assert.Equal(expected.GetProperty("elevationMeters").GetDouble(), actual.ElevationMeters);
            Assert.Equal(expected.GetProperty("moisture").GetDouble(), actual.Moisture);
            Assert.Equal(expected.GetProperty("fertility").GetDouble(), actual.Fertility);
            Assert.Equal(expected.GetProperty("biome").GetString(), actual.Biome);

            foreach (var resource in expected.GetProperty("resourcePotential").EnumerateObject())
            {
                Assert.Equal(resource.Value.GetDouble(), actual.ResourcePotential[resource.Name]);
            }

            Assert.Equal(expected.GetProperty("population").GetInt32(), actual.Population);
        }
    }

    [Fact]
    public async Task InitialWorldContainsTheSameStructuralEntitiesAsJavaScript()
    {
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);

        Assert.Equal(0, world.Day);
        Assert.Equal(6, world.Cities.Count);
        Assert.Equal(2, world.Actors.Count);
        Assert.Equal(7, world.Routes.Count);
        Assert.Equal(4, world.RandomStreams.Count);
        Assert.All(world.Cities.Values, city =>
        {
            Assert.Equal(content.Resources.Resources.Count, city.Stocks.Count);
            Assert.Equal(content.Resources.Resources.Count, city.Markets.Count);
            Assert.Equal(content.Technologies.Technologies.Count, city.TechnologyState.Count);
            Assert.Equal(
                world.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population,
                world.Spatial.Territories.Values
                    .Where(territory => territory.AssignedCityId == city.Id)
                    .Sum(territory => territory.Population));
        });

        var doctor = world.Actors["doctor_anna_lebedeva"];
        Assert.Equal("zone:16:48", doctor.Location.TerritoryId);
        Assert.Equal("greenfield", doctor.Location.CityId);
        Assert.Equal("city:greenfield", doctor.Location.SpatialNodeId);
        Assert.Equal(0.68, world.Routes[0].Condition);
    }

    [Fact]
    public async Task InitialWorldHashMatchesJavaScriptGoldenMaster()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        var expectedHash = CheckpointHash(fixture, 0);

        Assert.Equal(expectedHash, WorldSnapshot.Hash(world));
    }

    [Fact]
    public async Task DayOneHashMatchesJavaScriptGoldenMaster()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        SimulationEngine.Step(world, content);
        var expectedHash = CheckpointHash(fixture, 1);

        Assert.Equal(expectedHash, WorldSnapshot.Hash(world));
    }

    [Fact]
    public async Task DayThirtyHashMatchesJavaScriptGoldenMaster()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        SimulationEngine.Simulate(world, content, 30);
        var expectedHash = CheckpointHash(fixture, 30);

        Assert.Equal(expectedHash, WorldSnapshot.Hash(world));
    }

    [Fact]
    public async Task DayThreeHundredSixtyFiveHashMatchesJavaScriptGoldenMaster()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        SimulationEngine.Simulate(world, content, 365);
        var expectedHash = CheckpointHash(fixture, 365);

        Assert.Equal(expectedHash, WorldSnapshot.Hash(world));
    }

    [Theory]
    [InlineData(1825)]
    [InlineData(3650)]
    public async Task MultiYearHashMatchesJavaScriptGoldenMaster(int days)
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        SimulationEngine.Simulate(world, content, days);
        var expectedHash = CheckpointHash(fixture, days);

        Assert.Equal(expectedHash, WorldSnapshot.Hash(world));
    }

    [Fact]
    public async Task EveryMonthlyCheckpointMatchesJavaScriptUntilFiveYears()
    {
        using var fixture = await ReadFixtureAsync();
        var content = await ContentLoader.LoadAsync();
        var world = WorldFactory.Create(content);
        foreach (var checkpoint in fixture.RootElement.GetProperty("checkpoints").EnumerateArray()
                     .Where(item => item.GetProperty("day").GetInt32() <= 1825))
        {
            var day = checkpoint.GetProperty("day").GetInt32();
            SimulationEngine.Simulate(world, content, day - world.Day);
            Assert.True(checkpoint.GetProperty("hash").GetString() == WorldSnapshot.Hash(world),
                $"Первое расхождение на дне {day}");
        }
    }

    [Fact]
    public async Task RestoredWorldContinuesTheSameHistory()
    {
        var content = await ContentLoader.LoadAsync();
        var uninterrupted = WorldFactory.Create(content);
        var restoredSource = WorldFactory.Create(content);
        SimulationEngine.Simulate(restoredSource, content, 120);
        var restored = WorldSnapshot.Restore(content, WorldSnapshot.Create(restoredSource));

        Assert.Equal(WorldSnapshot.Hash(restoredSource), WorldSnapshot.Hash(restored));
        SimulationEngine.Simulate(uninterrupted, content, 365);
        SimulationEngine.Simulate(restored, content, 245);
        Assert.Equal(WorldSnapshot.Hash(uninterrupted), WorldSnapshot.Hash(restored));
    }

    private static string? CheckpointHash(JsonDocument fixture, int day) => fixture.RootElement
        .GetProperty("checkpoints").EnumerateArray().Single(item => item.GetProperty("day").GetInt32() == day)
        .GetProperty("hash").GetString();

    private static async Task<JsonDocument> ReadFixtureAsync()
    {
        var contentDirectory = ContentLoader.FindContentDirectory();
        var root = Directory.GetParent(contentDirectory)?.FullName ??
            throw new DirectoryNotFoundException("У content отсутствует родительский каталог");
        var fixturePath = Path.Combine(root, "tests", "fixtures", "js-parity-v1.json");
        await using var stream = File.OpenRead(fixturePath);
        return await JsonDocument.ParseAsync(stream);
    }
}
