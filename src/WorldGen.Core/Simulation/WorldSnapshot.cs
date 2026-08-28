using System.Text.Json;
using System.Text.Json.Nodes;
using WorldGen.Core.Serialization;
using WorldGen.Core.Spatial;
using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public static class WorldSnapshot
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static JsonObject Create(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var snapshot = new JsonObject
        {
            ["schemaVersion"] = world.SchemaVersion,
            ["scenarioId"] = world.ScenarioId,
            ["seed"] = world.Seed,
            ["contentFingerprint"] = world.ContentFingerprint,
            ["contentSchemaVersions"] = ToNode(world.ContentSchemaVersions),
            ["day"] = world.Day,
            ["calendar"] = ToNode(world.Calendar),
            ["nextEventId"] = world.NextEventId,
            ["nextShipmentId"] = world.NextShipmentId,
            ["reserveDays"] = world.ReserveDays,
            ["demographyPolicy"] = ToNode(world.DemographyPolicy),
            ["lodPolicy"] = ToNode(world.LodPolicy),
            ["spatial"] = CreateSpatial(world.Spatial),
            ["actors"] = ToNode(world.Actors),
            ["cities"] = ToNode(world.Cities),
            ["routes"] = ToNode(world.Routes),
            ["scheduledEvents"] = ToNode(world.ScheduledEvents),
            ["shipments"] = ToNode(world.Shipments),
            ["tradeIntents"] = CreateTradeIntents(world.TradeIntents),
            ["nextTradeIntentId"] = world.NextTradeIntentId,
            ["knowledgeTransfers"] = ToNode(world.KnowledgeTransfers),
            ["nextKnowledgeTransferId"] = world.NextKnowledgeTransferId,
            ["information"] = ToNode(world.Information),
            ["journal"] = ToNode(world.Journal),
            ["telemetry"] = ToNode(world.Telemetry),
            ["randomStreamStates"] = ToNode(world.RandomStreams
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value.State, StringComparer.Ordinal))
        };
        // Keep legacy snapshots byte-compatible when this optional subsystem is absent.
        if (world.SettlementDevelopment is not null) snapshot["settlementDevelopment"] = ToNode(world.SettlementDevelopment);
        return snapshot;
    }

    public static string Hash(WorldState world) => CanonicalJson.Hash(Create(world));

    public static WorldState Restore(ContentCatalog content, JsonObject snapshot)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(snapshot);
        var schemaVersion = snapshot["schemaVersion"]?.GetValue<int>();
        if (schemaVersion != 2) throw new InvalidOperationException($"Неподдерживаемая версия снимка мира '{schemaVersion}'");
        if (snapshot["scenarioId"]?.GetValue<string>() != content.Scenario.Id)
            throw new InvalidOperationException("Снимок относится к другому сценарию");
        if (snapshot["contentFingerprint"]?.GetValue<string>() != content.Fingerprint)
            throw new InvalidOperationException("Отпечаток контента снимка не совпадает с загруженными определениями");
        var versions = Required<ContentSchemaVersions>(snapshot, "contentSchemaVersions");
        var expectedVersions = new ContentSchemaVersions(content.Resources.SchemaVersion, content.Recipes.SchemaVersion,
            content.Technologies.SchemaVersion, content.Map.SchemaVersion, content.Scenario.SchemaVersion);
        if (versions != expectedVersions) throw new InvalidOperationException("Версии схем контента снимка не совпадают");

        var seed = snapshot["seed"]!.GetValue<uint>();
        var streamStates = snapshot["randomStreamStates"]!.AsObject();
        var streams = Determinism.SeededRandom.CreateStreams(seed, streamStates.Select(pair => pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in streamStates) streams[pair.Key].RestoreState(pair.Value!.GetValue<uint>());

        return new WorldState
        {
            ScenarioId = content.Scenario.Id,
            Seed = seed,
            ContentFingerprint = content.Fingerprint,
            ContentSchemaVersions = versions,
            Day = snapshot["day"]!.GetValue<int>(),
            Calendar = Required<CalendarDefinition>(snapshot, "calendar"),
            NextEventId = snapshot["nextEventId"]!.GetValue<int>(),
            NextShipmentId = snapshot["nextShipmentId"]!.GetValue<int>(),
            ReserveDays = snapshot["reserveDays"]!.GetValue<int>(),
            DemographyPolicy = Required<DemographyDefinition>(snapshot, "demographyPolicy"),
            LodPolicy = Required<LodPolicyDefinition>(snapshot, "lodPolicy"),
            Spatial = Required<SpatialHierarchy>(snapshot, "spatial"),
            Actors = Required<Dictionary<string, ActorState>>(snapshot, "actors"),
            Cities = Required<Dictionary<string, CityState>>(snapshot, "cities"),
            Routes = Required<List<RouteState>>(snapshot, "routes"),
            ScheduledEvents = Required<List<ScheduledEventDefinition>>(snapshot, "scheduledEvents"),
            Shipments = Required<List<ShipmentState>>(snapshot, "shipments"),
            TradeIntents = Required<List<TradeIntentState>>(snapshot, "tradeIntents"),
            NextTradeIntentId = snapshot["nextTradeIntentId"]!.GetValue<int>(),
            KnowledgeTransfers = Required<List<KnowledgeTransferState>>(snapshot, "knowledgeTransfers"),
            NextKnowledgeTransferId = snapshot["nextKnowledgeTransferId"]!.GetValue<int>(),
            Information = Required<InformationState>(snapshot, "information"),
            Journal = Required<List<JournalEvent>>(snapshot, "journal"),
            Telemetry = Required<TelemetryState>(snapshot, "telemetry"),
            SettlementDevelopment = snapshot["settlementDevelopment"]?.Deserialize<SettlementDevelopmentState>(SerializerOptions),
            RandomStreams = streams
        };
    }

    private static T Required<T>(JsonObject snapshot, string propertyName) where T : notnull
    {
        var node = snapshot[propertyName] ?? throw new InvalidOperationException($"В снимке отсутствует поле '{propertyName}'");
        return node.Deserialize<T>(SerializerOptions) ??
            throw new InvalidOperationException($"Не удалось восстановить поле '{propertyName}'");
    }

    private static JsonArray CreateTradeIntents(IEnumerable<TradeIntentState> intents)
    {
        var result = new JsonArray();
        foreach (var intent in intents)
        {
            var node = new JsonObject
            {
                ["id"] = intent.Id, ["kind"] = intent.Kind, ["cityId"] = intent.CityId,
                ["resourceId"] = intent.ResourceId, ["amount"] = intent.Amount, ["remaining"] = intent.Remaining,
                ["createdDay"] = intent.CreatedDay, ["availableDay"] = intent.AvailableDay,
                ["expiresDay"] = intent.ExpiresDay, ["limitPrice"] = intent.LimitPrice
            };
            if (intent.ShortfallEventId is not null) node["shortfallEventId"] = intent.ShortfallEventId;
            result.Add(node);
        }
        return result;
    }

    private static JsonObject CreateSpatial(SpatialHierarchy spatial)
    {
        var nodes = new JsonObject();
        foreach (var pair in spatial.Nodes)
        {
            nodes[pair.Key] = CreateSpatialNode(pair.Value);
        }

        return new JsonObject
        {
            ["regionNodeId"] = spatial.RegionNodeId,
            ["grid"] = ToNode(spatial.Grid),
            ["territories"] = ToNode(spatial.Territories),
            ["nodes"] = nodes
        };
    }

    private static JsonObject CreateSpatialNode(SpatialNode node) => node.Kind switch
    {
        "macro" => new JsonObject
        {
            ["id"] = node.Id,
            ["kind"] = node.Kind,
            ["grid"] = ToNode(node.Grid),
            ["parentNodeId"] = node.ParentNodeId,
            ["childTerritoryIds"] = ToNode(node.ChildTerritoryIds),
            ["dominantCityId"] = node.DominantCityId,
            ["aggregate"] = ToNode(node.Aggregate),
            ["detail"] = ToNode(node.Detail),
            ["activeUntilDay"] = node.ActiveUntilDay
        },
        "region" => new JsonObject
        {
            ["id"] = node.Id,
            ["kind"] = node.Kind,
            ["worldEntityId"] = node.WorldEntityId,
            ["name"] = node.Name,
            ["parentNodeId"] = null,
            ["childNodeIds"] = ToNode(node.ChildNodeIds),
            ["overlayNodeIds"] = ToNode(node.OverlayNodeIds),
            ["aggregate"] = ToNode(node.Aggregate)
        },
        "city" => new JsonObject
        {
            ["id"] = node.Id,
            ["kind"] = node.Kind,
            ["projection"] = node.Projection,
            ["worldEntityId"] = node.WorldEntityId,
            ["name"] = node.Name,
            ["parentNodeId"] = node.ParentNodeId,
            ["anchorTerritoryId"] = node.AnchorTerritoryId,
            ["childTerritoryIds"] = ToNode(node.ChildTerritoryIds),
            ["aggregate"] = ToNode(node.Aggregate),
            ["detail"] = ToNode(node.Detail),
            ["activeUntilDay"] = node.ActiveUntilDay
        },
        _ => throw new InvalidOperationException($"Неизвестный тип пространственной ноды '{node.Kind}'")
    };

    private static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, SerializerOptions);
}
