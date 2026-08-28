using System.Text.Json;
using System.Text.Json.Serialization;
using WorldGen.Core.Topology;

namespace WorldGen.Content;

public static class SphericalWorldLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static SphericalWorldLoader() => SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    public static async Task<SphericalWorldDefinition> LoadAsync(
        string? contentDirectory = null,
        string fileName = "cube-sphere-prototype.json",
        CancellationToken cancellationToken = default)
    {
        contentDirectory ??= ContentLoader.FindContentDirectory();
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ContentValidationException(nameof(fileName), "ожидалось имя файла без пути");

        var path = Path.Combine(contentDirectory, "worlds", fileName);
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var definition = JsonSerializer.Deserialize<SphericalWorldDefinition>(json, SerializerOptions) ??
                throw new JsonException("десериализация вернула null");
            Validate(definition, path);
            return definition;
        }
        catch (JsonException exception)
        {
            throw new ContentValidationException(path, $"не удалось разобрать JSON: {exception.Message}");
        }
    }

    private static void Validate(SphericalWorldDefinition definition, string path)
    {
        var errors = new List<string>();
        if (definition.Climate.LapseRatePerMeter is { } lapse && (!double.IsFinite(lapse) || lapse < 0 || lapse > .02))
            errors.Add("Некорректный высотный градиент температуры");
        if (definition.SchemaVersion != 1) errors.Add("schemaVersion должен быть равен 1");
        if (!string.Equals(definition.Topology, "cube_sphere", StringComparison.Ordinal))
            errors.Add("topology должен быть cube_sphere");
        if (definition.FaceSize < 2) errors.Add("faceSize должен быть не меньше 2");
        if (definition.ChunkSize < 1 || definition.ChunkSize > definition.FaceSize)
            errors.Add("chunkSize должен быть от 1 до faceSize");
        if (definition.FaceSize % definition.ChunkSize != 0)
            errors.Add("faceSize должен делиться на chunkSize без остатка");
        if (!double.IsFinite(definition.ZoneSizeMeters) || definition.ZoneSizeMeters <= 0)
            errors.Add("zoneSizeMeters должен быть положительным конечным числом");
        if (!double.IsFinite(definition.Terrain.ElevationRangeMeters) || definition.Terrain.ElevationRangeMeters <= 0)
            errors.Add("terrain.elevationRangeMeters должен быть положительным");
        if (definition.Terrain.Roughness is < 0 or > 1) errors.Add("terrain.roughness должен быть от 0 до 1");
        if (definition.Terrain.ForestThreshold is < 0 or > 1) errors.Add("terrain.forestThreshold должен быть от 0 до 1");
        if (definition.Climate.MoistureBase is < 0 or > 1) errors.Add("climate.moistureBase должен быть от 0 до 1");
        var settlementIds = new HashSet<string>(StringComparer.Ordinal);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var settlement in definition.Settlements)
        {
            if (string.IsNullOrWhiteSpace(settlement.Id) || !settlementIds.Add(settlement.Id))
                errors.Add("идентификаторы поселений должны быть непустыми и уникальными");
            if (string.IsNullOrWhiteSpace(settlement.Name)) errors.Add($"settlements.{settlement.Id}.name не заполнено");
            foreach (var building in settlement.Buildings)
            {
                if (string.IsNullOrWhiteSpace(building.Id) || !assetIds.Add($"building:{building.Id}"))
                    errors.Add("идентификаторы построек должны быть непустыми и уникальными");
                if (building.InfluenceStrength <= 0 || building.Footprint.Count == 0)
                    errors.Add($"постройка {building.Id} должна иметь влияние и контур");
                foreach (var allocation in building.Footprint)
                    ValidateCell(allocation.Face, allocation.X, allocation.Y, allocation.CapacityUnits, $"building.{building.Id}");
            }
            foreach (var land in settlement.UsedLands)
            {
                if (string.IsNullOrWhiteSpace(land.Id) || !assetIds.Add($"land:{land.Id}"))
                    errors.Add("идентификаторы угодий должны быть непустыми и уникальными");
                if (land.Kind is not ("cultivated_field" or "pasture" or "orchard"))
                    errors.Add($"неизвестный вид угодья {land.Kind}");
                if (land.Usage is <= 0 or > 1 || land.InfluenceStrength <= 0)
                    errors.Add($"угодье {land.Id} должно иметь положительные usage и influenceStrength");
                ValidateCell(land.Face, land.X, land.Y, 1, $"land.{land.Id}");
            }
        }

        if (errors.Count > 0) throw new ContentValidationException(path, string.Join("; ", errors));
        return;

        void ValidateCell(CubeFace face, int x, int y, int capacity, string itemPath)
        {
            if (!Enum.IsDefined(face) || (uint)x >= definition.FaceSize || (uint)y >= definition.FaceSize)
                errors.Add($"{itemPath} находится вне мира");
            if (capacity < 1) errors.Add($"{itemPath}.capacityUnits должен быть положительным");
        }
    }
}
