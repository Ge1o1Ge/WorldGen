using WorldGen.Core.Content;
using WorldGen.Core.Determinism;
using WorldGen.Core.Spatial;
using System.Text.Json.Nodes;

namespace WorldGen.Core.Simulation;

public sealed class WorldState
{
    public int SchemaVersion { get; init; } = 2;
    public required string ScenarioId { get; init; }
    public required uint Seed { get; init; }
    public required string ContentFingerprint { get; init; }
    public required ContentSchemaVersions ContentSchemaVersions { get; init; }
    public int Day { get; set; }
    public required CalendarDefinition Calendar { get; init; }
    public int NextEventId { get; set; } = 1;
    public int NextShipmentId { get; set; } = 1;
    public required int ReserveDays { get; init; }
    public required DemographyDefinition DemographyPolicy { get; init; }
    public required LodPolicyDefinition LodPolicy { get; init; }
    public required SpatialHierarchy Spatial { get; init; }
    public required Dictionary<string, ActorState> Actors { get; init; }
    public required Dictionary<string, CityState> Cities { get; init; }
    public required List<RouteState> Routes { get; init; }
    public required IReadOnlyList<ScheduledEventDefinition> ScheduledEvents { get; init; }
    public List<ShipmentState> Shipments { get; set; } = [];
    public List<TradeIntentState> TradeIntents { get; set; } = [];
    public int NextTradeIntentId { get; set; } = 1;
    public List<KnowledgeTransferState> KnowledgeTransfers { get; set; } = [];
    public int NextKnowledgeTransferId { get; set; } = 1;
    public InformationState Information { get; set; } = new();
    public List<JournalEvent> Journal { get; set; } = [];
    public TelemetryState Telemetry { get; set; } = new();
    public SettlementDevelopmentState? SettlementDevelopment { get; set; }
    public required Dictionary<string, SeededRandom> RandomStreams { get; init; }
}

public sealed record ContentSchemaVersions(int Resources, int Recipes, int Technologies, int Map, int Scenario);

public sealed record CityState
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SpatialNodeId { get; init; }
    public required double WorkerShare { get; init; }
    public required double FoodPerPersonPerDay { get; init; }
    public required int LocalReserveDays { get; set; }
    public required Dictionary<string, double> Stocks { get; init; }
    public required Dictionary<string, MarketState> Markets { get; init; }
    public required List<IndustryState> Industries { get; init; }
    public required List<InstitutionState> Institutions { get; init; }
    public required Dictionary<string, ActiveEffectState> ActiveEffects { get; init; }
    public required Dictionary<string, string> ResourceSignals { get; init; }
    public required CityKnowledgeState KnowledgeState { get; init; }
    public required Dictionary<string, TechnologyState> TechnologyState { get; init; }
    public required CityDemographyState Demography { get; init; }
    public required InfrastructureState Infrastructure { get; init; }
    public required Dictionary<string, NeedState> Needs { get; init; }
    public required ShortageState Shortage { get; init; }
}

public sealed record MarketState
{
    public required double Price { get; set; }
    public double TargetStock { get; set; }
    public double? CoverageDays { get; set; }
    public double Availability { get; set; } = 1;
    public bool ShockActive { get; set; }
    public string? ShockEventId { get; set; }
}

public sealed record IndustryState
{
    public required string Id { get; init; }
    public required string RecipeId { get; init; }
    public required double Capacity { get; set; }
    public required GridCoordinate Zone { get; init; }
    public required string ZoneId { get; init; }
    public required double InitialCapacity { get; init; }
    public string? LastConstraintKey { get; set; }
    public string? ConstraintEventId { get; set; }
    public double TotalBatches { get; set; }
}

public sealed record InstitutionState
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required double Competence { get; init; }
    public required double LearningRate { get; init; }
    public required IReadOnlyList<string> Priorities { get; init; }
    public int Decisions { get; set; }
}

public sealed record TechnologyState
{
    public required double Knowledge { get; set; }
    public required double Competence { get; set; }
    public required double Capability { get; set; }
    public required double Adoption { get; set; }
    public required Dictionary<string, int> Milestones { get; init; }
}

public sealed record CityKnowledgeState
{
    public Dictionary<string, ObservationState> Observations { get; init; } = new(StringComparer.Ordinal);
    // Optional for the legacy regional parity fixture. This is information about
    // places, not the technology/competence/adoption state of this city.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, SettlementKnowledge>? KnownSettlements { get; set; }
}

public sealed record CityDemographyState
{
    public double Health { get; set; } = 0.78;
    public int Births { get; set; }
    public int Deaths { get; set; }
    public int Immigration { get; set; }
    public int Emigration { get; set; }
    public double BirthRemainder { get; set; }
    public double DeathRemainder { get; set; }
}

public sealed record InfrastructureState
{
    public double HousingCondition { get; set; } = 0.72;
    public double RoadCondition { get; set; } = 0.58;
    public double Sanitation { get; set; } = 0.62;
}

public record NeedState
{
    public bool Active { get; set; }
    public int Days { get; set; }
    public int EpisodeDays { get; set; }
    public int MissingStreak { get; set; }
    public int SatisfiedStreak { get; set; }
    public double TotalMissing { get; set; }
    public string? EventId { get; set; }
}

public sealed record ShortageState
{
    public bool Active { get; set; }
    public int Days { get; set; }
    public int EpisodeDays { get; set; }
    public int MissingStreak { get; set; }
    public int SatisfiedStreak { get; set; }
    public double TotalFoodMissing { get; set; }
    public string? EventId { get; set; }
}

public sealed record ActorState
{
    public required string Id { get; init; }
    public string Kind { get; init; } = "person";
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required ActorLocation Location { get; init; }
    public required ActorImportance Importance { get; init; }
    public bool RepresentedInPopulation { get; init; } = true;
    public required ActorProvenance Provenance { get; init; }
    public IReadOnlyDictionary<string, object> KnowledgeState { get; init; } = new Dictionary<string, object>();
}

public sealed record ActorLocation(string TerritoryId, string CityId, string SpatialNodeId);

public sealed record ActorImportance(double Score, IReadOnlyList<string> Reasons);

public sealed record ActorProvenance(string Type, string? CauseEventId);

public sealed record RouteState
{
    public required string Id { get; init; }
    public required string A { get; init; }
    public required string B { get; init; }
    public required int TravelDays { get; set; }
    public required double DailyCapacity { get; set; }
    public required int BaseTravelDays { get; init; }
    public required double BaseDailyCapacity { get; init; }
    public double Condition { get; set; } = 0.68;
}

public sealed record InformationState
{
    public List<InformationReport> Reports { get; set; } = [];
    public int NextReportId { get; set; } = 1;
    public int LastJournalIndex { get; set; }
}

public sealed record TelemetryState
{
    public List<DailyTelemetry> Daily { get; set; } = [];
}

public sealed record ActiveEffectState(double Multiplier, int EndDay, string StartEventId, string TerritoryId, string Label, bool Endogenous);
public sealed record ObservationState(string EventId, string SourceCityId, int ReceivedDay, double Confidence, string Channel, string? ReportId);
public sealed record JournalEvent(string Id, int Day, string Type, string? SubjectId, List<string> CauseIds, JsonObject Details);
public sealed record ShipmentState(string Id, string From, string To, string ResourceId, double Amount, List<string> RouteIds, int DepartureDay, int ArrivalDay, string DispatchEventId);
public sealed record TradeIntentState
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string CityId { get; init; }
    public required string ResourceId { get; init; }
    public required double Amount { get; init; }
    public required double Remaining { get; set; }
    public required int CreatedDay { get; init; }
    public required int AvailableDay { get; init; }
    public required int ExpiresDay { get; init; }
    public required double LimitPrice { get; init; }
    public string? ShortfallEventId { get; set; }
}
public sealed record KnowledgeTransferState(string Id, string TechnologyId, string From, string To, double Amount, int DepartureDay, int ArrivalDay, string RouteId, string? CauseEventId);
public sealed record InformationReport(string Id, string EventId, string EventType, int EventDay, string SourceCityId, string To, int DepartureDay, int ArrivalDay, double Confidence, string Channel,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SettlementKnowledge? Settlement = null);
public sealed record DailyTelemetry
{
    public required int Day { get; init; }
    public Dictionary<string, double> ProductionByResource { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> IndustrialConsumptionByResource { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> DecayedByResource { get; init; } = new(StringComparer.Ordinal);
    public double HouseholdFoodConsumed { get; set; }
    public double HouseholdFoodMissing { get; set; }
    public Dictionary<string, double> HouseholdConsumptionByResource { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> HouseholdMissingByResource { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> InfrastructureConsumptionByResource { get; init; } = new(StringComparer.Ordinal);
    public int ShipmentsDispatched { get; set; }
    public int ShipmentsArrived { get; set; }
}
