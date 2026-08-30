using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public sealed record PrimitiveTechnologyRule(string Id, string Name, string Domain, bool Baseline,
    string[] Prerequisites, string Practice, double PracticeHours,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Staged = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? AnyPrerequisites = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string[] AlternativePrerequisites => AnyPrerequisites ?? [];
}

/// <summary>A small-scale process available to households without a pre-seeded regional industry.</summary>
public sealed record PrimitiveProcessRule(string Id, string Name, string Technology, string Practice,
    IReadOnlyDictionary<string, double> Inputs, IReadOnlyDictionary<string, double> RequiredStocks,
    IReadOnlyDictionary<string, double> Outputs, string TargetResource, double LaborHoursPerBatch, double TargetOutputPerPerson,
    double MaximumBatchesPerPersonPerDay, int Priority = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string[]? RequiredBuildings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, double>? BuildingLaborMultipliers = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string[] BuildingRequirements => RequiredBuildings ?? [];
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyDictionary<string, double> LaborMultipliers => BuildingLaborMultipliers ?? EmptyMultipliers;
    private static readonly IReadOnlyDictionary<string, double> EmptyMultipliers = new Dictionary<string, double>();
}

public sealed record PrimitiveWorldRules
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BiosphereRules? Biosphere { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WinterRules? Winter { get; init; }
    public required PrimitiveTechnologyRule[] Technologies { get; init; }
    public required ResourceDefinition[] Resources { get; init; }
    public required HouseholdActivityRule[] Activities { get; init; }
    public TechnologyRelation[] Relations { get; init; } = [];
    public PrimitiveProcessRule[] Processes { get; init; } = [];
    public BuildingMaterialRule[] Materials { get; init; } = [];
    public double FreshFoodDecay { get; init; } = .025;
    public double WinterReserveDays { get; init; } = 60;
    public double PreparationDays { get; init; } = 100;
    public double StorageFoodPerResident { get; init; } = .06;
    public double PreserveFoodPerHour { get; init; } = .003;
    public double PreserveFuelPerFood { get; init; } = .12;
    public double ChiefDelegationShare { get; init; } = .25;
    public double SeasonalAmplitudeC { get; init; } = 9;
    public int AtmosphericSystems { get; init; } = 12;
    public double WeatherSpeedRadians { get; init; } = .022;
    public double LightningIgnitionChance { get; init; } = .015;
    public void Validate()
    {
        Winter?.Validate();
        Biosphere?.Validate();
        var ids = Technologies.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != Technologies.Length || !ids.Contains("calendar") ||
            Technologies.Any(t => string.IsNullOrWhiteSpace(t.Id) || t.Prerequisites.Concat(t.AlternativePrerequisites).Any(p => !ids.Contains(p)) ||
                !double.IsFinite(t.PracticeHours) || t.PracticeHours <= 0) ||
            Relations.Any(relation => !ids.Contains(relation.From) || !ids.Contains(relation.To) || relation.From == relation.To ||
                relation.Type is not ("helps" or "enables" or "supports" or "industrial")) ||
            Resources.Select(resource => resource.Id).Distinct(StringComparer.Ordinal).Count() != Resources.Length ||
            Resources.Any(resource => string.IsNullOrWhiteSpace(resource.Id) || string.IsNullOrWhiteSpace(resource.Name) ||
                string.IsNullOrWhiteSpace(resource.Unit) || string.IsNullOrWhiteSpace(resource.Category) ||
                !double.IsFinite(resource.BaseValue) || resource.BaseValue <= 0 || !double.IsFinite(resource.DecayPerDay) ||
                resource.DecayPerDay < 0 || resource.DecayPerDay >= 1 || !double.IsFinite(resource.FoodValue) || resource.FoodValue < 0) ||
            !double.IsFinite(FreshFoodDecay) || FreshFoodDecay <= 0 || FreshFoodDecay >= 1 ||
            !double.IsFinite(WinterReserveDays) || WinterReserveDays <= 0 || WinterReserveDays > 180 ||
            !double.IsFinite(PreparationDays) || PreparationDays < 1 || PreparationDays > 180 ||
            !double.IsFinite(StorageFoodPerResident) || StorageFoodPerResident <= 0 ||
            !double.IsFinite(PreserveFoodPerHour) || PreserveFoodPerHour <= 0 ||
            !double.IsFinite(PreserveFuelPerFood) || PreserveFuelPerFood <= 0 ||
            !double.IsFinite(ChiefDelegationShare) || ChiefDelegationShare < 0 || ChiefDelegationShare > .5 ||
            !double.IsFinite(SeasonalAmplitudeC) || SeasonalAmplitudeC < 0 || SeasonalAmplitudeC > 20 ||
            AtmosphericSystems is < 1 or > 32 || !double.IsFinite(WeatherSpeedRadians) || WeatherSpeedRadians <= 0 || WeatherSpeedRadians > .1 ||
            !double.IsFinite(LightningIgnitionChance) || LightningIgnitionChance < 0 || LightningIgnitionChance > 1 ||
            Processes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != Processes.Length || Processes.Any(p =>
                string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || !ids.Contains(p.Technology) ||
                string.IsNullOrWhiteSpace(p.Practice) || p.Inputs.Count == 0 || p.Outputs.Count == 0 ||
                string.IsNullOrWhiteSpace(p.TargetResource) || !p.Outputs.ContainsKey(p.TargetResource) ||
                p.Inputs.Keys.Concat(p.RequiredStocks.Keys).Concat(p.Outputs.Keys).Any(string.IsNullOrWhiteSpace) ||
                p.Inputs.Values.Concat(p.RequiredStocks.Values).Concat(p.Outputs.Values).Any(n => !double.IsFinite(n) || n <= 0) ||
                p.BuildingRequirements.Any(string.IsNullOrWhiteSpace) || p.BuildingRequirements.Distinct(StringComparer.Ordinal).Count() != p.BuildingRequirements.Length ||
                p.LaborMultipliers.Any(pair => !p.BuildingRequirements.Contains(pair.Key, StringComparer.Ordinal) || !double.IsFinite(pair.Value) || pair.Value <= 0 || pair.Value > 4) ||
                !double.IsFinite(p.LaborHoursPerBatch) || p.LaborHoursPerBatch <= 0 ||
                !double.IsFinite(p.TargetOutputPerPerson) || p.TargetOutputPerPerson <= 0 ||
                !double.IsFinite(p.MaximumBatchesPerPersonPerDay) || p.MaximumBatchesPerPersonPerDay <= 0))
            throw new InvalidOperationException("Некорректный стартовый блок");
        var visiting = new HashSet<string>(); var done = new HashSet<string>();
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Цикл начальных технологий");
            foreach (var p in Technologies.Single(t => t.Id == id).Prerequisites) Visit(p);
            visiting.Remove(id); done.Add(id);
        }
        foreach (var id in ids) Visit(id);
        var reachable = Technologies.Where(t => t.Baseline).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var added = Technologies.Where(t => !reachable.Contains(t.Id) && t.Prerequisites.All(reachable.Contains) &&
                (t.AlternativePrerequisites.Length == 0 || t.AlternativePrerequisites.Any(reachable.Contains)))
                .Select(t => t.Id).ToArray();
            if (added.Length == 0) break;
            reachable.UnionWith(added);
        }
        if (reachable.Count != Technologies.Length) throw new InvalidOperationException("Недостижимая комбинация обязательных и альтернативных технологий");
    }
}
