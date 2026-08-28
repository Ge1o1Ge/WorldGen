using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public sealed record PrimitiveTechnologyRule(string Id, string Name, string Domain, bool Baseline,
    string[] Prerequisites, string Practice, double PracticeHours);

public sealed record PrimitiveWorldRules
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BiosphereRules? Biosphere { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WinterRules? Winter { get; init; }
    public required PrimitiveTechnologyRule[] Technologies { get; init; }
    public required ResourceDefinition[] Resources { get; init; }
    public required HouseholdActivityRule[] Activities { get; init; }
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
            Technologies.Any(t => string.IsNullOrWhiteSpace(t.Id) || t.Prerequisites.Any(p => !ids.Contains(p)) ||
                !double.IsFinite(t.PracticeHours) || t.PracticeHours <= 0) ||
            !double.IsFinite(FreshFoodDecay) || FreshFoodDecay <= 0 || FreshFoodDecay >= 1 ||
            !double.IsFinite(WinterReserveDays) || WinterReserveDays <= 0 || WinterReserveDays > 180 ||
            !double.IsFinite(PreparationDays) || PreparationDays < 1 || PreparationDays > 180 ||
            !double.IsFinite(StorageFoodPerResident) || StorageFoodPerResident <= 0 ||
            !double.IsFinite(PreserveFoodPerHour) || PreserveFoodPerHour <= 0 ||
            !double.IsFinite(PreserveFuelPerFood) || PreserveFuelPerFood <= 0 ||
            !double.IsFinite(ChiefDelegationShare) || ChiefDelegationShare < 0 || ChiefDelegationShare > .5 ||
            !double.IsFinite(SeasonalAmplitudeC) || SeasonalAmplitudeC < 0 || SeasonalAmplitudeC > 20 ||
            AtmosphericSystems is < 1 or > 32 || !double.IsFinite(WeatherSpeedRadians) || WeatherSpeedRadians <= 0 || WeatherSpeedRadians > .1 ||
            !double.IsFinite(LightningIgnitionChance) || LightningIgnitionChance < 0 || LightningIgnitionChance > 1)
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
    }
}
