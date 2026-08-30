using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

// Only materialized households and used resource patches are kept, never one agent per person.
public sealed class SettlementDevelopmentState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AtmosphereState? Atmosphere { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public bool IsForager { get; set; }
    public int NextBuildingId { get; set; } = 1;
    public Dictionary<string, SettlementLifeState> Cities { get; set; } = new(StringComparer.Ordinal);
    public List<DwellingState> Buildings { get; set; } = [];
    public Dictionary<string, Dictionary<string, double>> WildStocks { get; set; } = new(StringComparer.Ordinal);
    public List<LocalTrailState> Trails { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScoutingState? Scouting { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, double>>? HarvestPressure { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<WildlifeGroupState>? Wildlife { get; set; }
}

public sealed class WildlifeGroupState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeciesId { get; set; }
    public required string Id { get; init; }
    public required CellAddress Center { get; set; }
    public CellAddress PreviousCenter { get; set; }
    public int RadiusCells { get; init; }
    public double Biomass { get; set; }
    public double Capacity { get; init; }
    public double Alert { get; set; }
    public CellAddress? Threat { get; set; }
    public int LastHuntedDay { get; set; } = -1;
    public int LastMoveDay { get; set; } = -1;
    public long Moves { get; set; }
    public double Harvested { get; set; }
    public double Regrown { get; set; }
}

public sealed class SettlementLifeState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BiologyState? Biology { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PrimitiveSettlementState? Primitive { get; set; }
    public int HousingCapacity { get; set; }
    public int Unhoused { get; set; }
    public int LastRelocationDay { get; set; } = -1000;
    public double LaborAvailableHours { get; set; }
    public double LaborUsedHours { get; set; }
    public double WaterTravelHours { get; set; }
    public double WaterCollected { get; set; }
    public double WaterCoverage { get; set; }
    public double IndustryLaborHours { get; set; }
    public Dictionary<string, double> Production { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> PracticeHours { get; set; } = new(StringComparer.Ordinal);
    // Practice from before prerequisites became known is general domain experience,
    // not retroactive practice of the newly available method.
    public Dictionary<string, double> TechnologyPracticeBaselines { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PrimitiveProcessState> Processes { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Discoveries { get; set; } = new(StringComparer.Ordinal);
    public List<HouseholdTaskState> Tasks { get; set; } = [];
    public string Decision { get; set; } = "Начальная стоянка";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SettlementSupplyState? Supply { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CollectiveDecisionState? Council { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HouseholdFoodState? Food { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SettlementMaintenanceState? Maintenance { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SettlementWellbeingState? Wellbeing { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SettlementStorageState? Storage { get; set; }
}

public sealed class PrimitiveProcessState
{
    public int LastDay { get; set; } = -1;
    public double BatchesToday { get; set; }
    public double LaborHoursToday { get; set; }
    public double TotalBatches { get; set; }
    public string? Constraint { get; set; }
    public string? BuildingId { get; set; }
    public double LaborMultiplier { get; set; } = 1;
}

public sealed class SettlementStorageState
{
    public double TotalCapacity { get; set; }
    public double UsedVolume { get; set; }
    public double OutdoorVolume { get; set; }
    public double LostToday { get; set; }
    public Dictionary<string, double> CapacityByBuildingKind { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> UsedByBuildingKind { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> StoredByResource { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> SpecializedByResource { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> OutdoorByResource { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> LostByResource { get; set; } = new(StringComparer.Ordinal);
}

public sealed class HouseholdFoodState
{
    public double LaborHours { get; set; }
    public double TravelHours { get; set; }
    public double WildOutput { get; set; }
    public double WildHours { get; set; }
    public double MeanOneWayMeters { get; set; }
    public double GardenOutput { get; set; }
    public int ReadyGardens { get; set; }
    public int PreparingGardens { get; set; }
    public int MovedToday { get; set; }
}

public sealed class SettlementSupplyState
{
    public List<SupplyDay> History { get; set; } = [];
    public double FoodReserveDays { get; set; }
    public double FoodRenewalPerDay { get; set; }
    public double RenewalCoverage { get; set; }
    public double NaturalFoodStockDays { get; set; }
    public double LaborShare { get; set; }
    public double WaterCoverage { get; set; }
    public int AccessibleCells { get; set; }
    public int PressureStreak { get; set; }
    public string? PressureEventId { get; set; }
    public int LastDepartureDay { get; set; } = -100000;
    public int ScoutPeopleToday { get; set; }
    public double ScoutLaborHours { get; set; }
    public string Reason { get; set; } = "Накопление наблюдений о снабжении";
    public string Action { get; set; } = "Оценка окрестностей";
    public List<ScoutReport> Reports { get; set; } = [];
}
public sealed record SupplyDay(int Day, double FoodReserveDays, double LaborShare, double WaterCoverage, double RenewalCoverage);
public sealed class ScoutingState
{
    public int NextId { get; set; } = 1;
    public Dictionary<string, HashSet<string>> KnownCells { get; set; } = new(StringComparer.Ordinal);
    // Council sectors from recent departures. They are bounded and used to
    // prevent a settlement from repeatedly approving the same bearing.
    public Dictionary<string, List<UnitVector3>> RecentDirections { get; set; } = new(StringComparer.Ordinal);
    // Only the current/latest expedition of each city. Long paths do not accumulate forever.
    public List<ScoutExpedition> Expeditions { get; set; } = [];
}
public sealed class ScoutExpedition
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FoodComposition? ProvisionComposition { get; set; }
    public required string Id { get; init; }
    public required string CityId { get; init; }
    public required CellAddress Home { get; init; }
    public required CellAddress Current { get; set; }
    public required UnitVector3 Direction { get; init; }
    public int People { get; set; }
    public int InitialPeople { get; init; }
    public int DepartureDay { get; init; }
    public int LastStepDay { get; set; } = -1;
    public int? ReturnDay { get; set; }
    public string Phase { get; set; } = "outbound";
    public string TravelMode { get; set; } = "foot";
    public string CurrentInterest { get; set; } = "Общий курс совета";
    public string Reason { get; init; } = "";
    public string? CauseEventId { get; set; }
    public string? DecisionId { get; set; }
    public int ProvisionDays { get; init; }
    public int PlannedOutboundDays { get; set; }
    public int ExtensionDays { get; set; }
    public double CargoCapacity { get; init; }
    public double RaftTimber { get; set; }
    public bool RaftReady { get; init; }
    public double SpeedMultiplier { get; init; } = 1;
    public double Food { get; set; }
    public double Water { get; set; }
    public double ForagedFood { get; set; }
    public double RefilledWater { get; set; }
    public int LastResupplyDay { get; set; } = -1;
    public double HazardExposure { get; set; }
    public int Casualties { get; set; }
    public int? LostDay { get; set; }
    public List<CellAddress> Path { get; set; } = [];
    public List<CellAddress> LastLeg { get; set; } = [];
    public List<ScoutObservation> Observations { get; set; } = [];
    public Dictionary<string, double> SeedSamples { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> CapturedAnimals { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> CaptureAttempts { get; set; } = new(StringComparer.Ordinal);
    public int ReturnIndex { get; set; }
    public double CargoUsed => Food + Water + RaftTimber + SeedSamples.Values.Sum();
}
public sealed record ScoutTerrain(bool Water, bool FreshWater, double Elevation, double Temperature, double Moisture, double Forest, double FoodRenewalPerDay);
public sealed record ScoutObservation(CellAddress Cell, int ObservedDay, bool FreshWater, double FoodRenewalPerDay,
    IReadOnlyList<string>? Plants = null, IReadOnlyList<string>? Animals = null, string? ObservedClaim = null);
public sealed record ScoutReport(string ExpeditionId, int DepartureDay, int ReceivedDay, int SurveyedCells,
    IReadOnlyList<ScoutObservation> Candidates, string Outcome, IReadOnlyList<string>? Plants = null,
    IReadOnlyList<string>? Animals = null, IReadOnlyDictionary<string, int>? CapturedAnimals = null, int Casualties = 0,
    IReadOnlyDictionary<string, int>? ForeignClaims = null);

public sealed class DwellingState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BuildingLifecycleState? Lifecycle { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WellStorageState? Well { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FieldLifecycleState? Field { get; set; }
    public required string Id { get; init; }
    public required string CityId { get; init; }
    public required string Kind { get; init; }
    public required CellAddress Cell { get; init; }
    public int Slot { get; init; }
    public string Status { get; set; } = "active";
    public int Residents { get; set; }
    public double LaborDone { get; set; }
    public int UnusedDays { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ReadyDay { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HouseholdId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? MoveFinished { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? RequiredLaborHours { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? ClearingRemaining { get; set; }
    public string? Replaces { get; init; }
    public string? CauseEventId { get; set; }
}

public sealed record HouseholdTaskState(string HomeId, string Activity, CellAddress Destination, double Hours, double Output);
public sealed class LocalTrailState
{
    public required CellAddress From { get; init; }
    public required CellAddress To { get; init; }
    public double Strength { get; set; }
    public double Passages { get; set; }
}
