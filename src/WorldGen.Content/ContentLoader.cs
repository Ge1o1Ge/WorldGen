using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WorldGen.Core.Content;
using WorldGen.Core.Serialization;

namespace WorldGen.Content;

public static class ContentLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<ContentCatalog> LoadAsync(
        string? contentDirectory = null,
        string scenarioName = "regional-smoke.json",
        CancellationToken cancellationToken = default)
    {
        contentDirectory ??= FindContentDirectory();
        EnsureSimpleFileName(scenarioName, nameof(scenarioName));

        var scenarioNode = await ReadObjectAsync(
            Path.Combine(contentDirectory, "scenarios", scenarioName), cancellationToken);
        var scenario = Deserialize<ScenarioDocument>(scenarioNode, "scenario");
        EnsureSimpleFileName(scenario.MapFile, "scenario.mapFile");

        var resourcesTask = ReadObjectAsync(Path.Combine(contentDirectory, "resources.json"), cancellationToken);
        var recipesTask = ReadObjectAsync(Path.Combine(contentDirectory, "recipes.json"), cancellationToken);
        var technologiesTask = ReadObjectAsync(Path.Combine(contentDirectory, "technologies.json"), cancellationToken);
        var mapTask = ReadObjectAsync(Path.Combine(contentDirectory, "maps", scenario.MapFile), cancellationToken);
        await Task.WhenAll(resourcesTask, recipesTask, technologiesTask, mapTask);

        var resourcesNode = await resourcesTask;
        var recipesNode = await recipesTask;
        var technologiesNode = await technologiesTask;
        var mapNode = await mapTask;
        var resources = Deserialize<ResourceDocument>(resourcesNode, "resources");
        var recipes = Deserialize<RecipeDocument>(recipesNode, "recipes");
        var technologies = Deserialize<TechnologyDocument>(technologiesNode, "technologies");
        var map = Deserialize<MapDocument>(mapNode, "map");

        var raw = new JsonObject
        {
            ["resources"] = resourcesNode,
            ["recipes"] = recipesNode,
            ["technologies"] = technologiesNode,
            ["map"] = mapNode,
            ["scenario"] = scenarioNode
        };
        var catalog = new ContentCatalog(
            resources, recipes, technologies, map, scenario, raw, CanonicalJson.Hash(raw));
        ContentValidator.Validate(catalog);
        return catalog;
    }

    public static string FindContentDirectory()
    {
        foreach (var origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "content");
                if (File.Exists(Path.Combine(candidate, "resources.json"))) return candidate;
            }
        }

        throw new DirectoryNotFoundException("Не удалось найти папку content от текущего каталога или каталога приложения");
    }

    private static async Task<JsonObject> ReadObjectAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, cancellationToken);
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            }) as JsonObject ?? throw new JsonException("ожидался корневой объект");
        }
        catch (JsonException exception)
        {
            throw new ContentValidationException(filePath, $"не удалось разобрать JSON: {exception.Message}");
        }
    }

    private static T Deserialize<T>(JsonObject node, string path)
    {
        try
        {
            return node.Deserialize<T>(SerializerOptions) ??
                throw new JsonException("десериализация вернула null");
        }
        catch (JsonException exception)
        {
            throw new ContentValidationException(path, exception.Message);
        }
    }

    private static void EnsureSimpleFileName(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.GetFileName(value) != value)
        {
            throw new ContentValidationException(path, "ожидалось имя файла без пути");
        }
    }
}
