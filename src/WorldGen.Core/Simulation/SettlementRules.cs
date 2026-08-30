using WorldGen.Core.Content;
using System.Text.Json.Serialization;

namespace WorldGen.Core.Simulation;

// Editable, deliberately game-scale quantities; not archaeological estimates.
public sealed record SettlementRules
{
    public required int SchemaVersion { get; init; }
    public required int ResidentsPerHouse { get; init; }
    public required double WorkHoursPerDay { get; init; }
    public required double WalkingMetersPerHour { get; init; }
    public required double CarryWaterTonnes { get; init; }
    public required int ReserveDays { get; init; }
    public required int StalledConstructionAbandonAfterDays { get; init; }
    public required int RelocationCooldownDays { get; init; }
    public required IReadOnlyList<ResourceDefinition> Resources { get; init; }
    public required IReadOnlyList<NaturalPoolRule> NaturalPools { get; init; }
    public required IReadOnlyList<HouseholdActivityRule> Activities { get; init; }
    public required IReadOnlyList<SettlementBuildingRule> Buildings { get; init; }
    public required IReadOnlyList<SettlementDiscoveryRule> Discoveries { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementTrailRules? Trails { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementExplorationRules? Exploration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionRules? Decisions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementSubsistenceRules? Subsistence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementLifecycleRules? Lifecycle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementWellbeingRules? Wellbeing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettlementStorageRules? Storage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrimitiveWorldRules? Primitive { get; init; }

    public void Validate()
    {
        Trails?.Validate();
        Exploration?.Validate();
        Decisions?.Validate();
        Subsistence?.Validate();
        Lifecycle?.Validate();
        Wellbeing?.Validate();
        Storage?.Validate();
        Primitive?.Validate();
        if (Wellbeing is { } wellbeing && (wellbeing.SafetyFoodDays >= ReserveDays || wellbeing.Foods.SelectMany(f => f.Activities)
            .Any(id => id != "cultivate" && !Activities.Any(a => a.Id == id && a.Output == "food"))))
            throw new InvalidOperationException("Неизвестный источник рациона или небезопасный резерв пищи");
        if (SchemaVersion != 1 || ResidentsPerHouse < 1 || WorkHoursPerDay <= 0 || WorkHoursPerDay > 24 ||
            !double.IsFinite(WorkHoursPerDay) || WalkingMetersPerHour <= 0 || !double.IsFinite(WalkingMetersPerHour) ||
            CarryWaterTonnes <= 0 || !double.IsFinite(CarryWaterTonnes) || ReserveDays < 1 ||
            StalledConstructionAbandonAfterDays < 1 || RelocationCooldownDays < 1)
            throw new InvalidOperationException("Некорректные нормы поселения");
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        static bool Unique(IEnumerable<string> ids) { var a = ids.ToArray(); return a.All(id => !string.IsNullOrWhiteSpace(id)) && a.Distinct().Count() == a.Length; }
        if (!Unique(Resources.Select(x => x.Id)) || !Unique(NaturalPools.Select(x => x.Id)) || !Unique(Activities.Select(x => x.Id)) ||
            !Unique(Buildings.Select(x => x.Id)) || !Unique(Discoveries.Select(x => x.Id)) ||
            NaturalPools.Any(x => !Positive(x.Capacity) || !double.IsFinite(x.RecoveryPerDay) || x.RecoveryPerDay < 0 || x.RecoveryPerDay > 1 || !x.Renewable && x.RecoveryPerDay != 0) ||
            Activities.Any(x => !Positive(x.OutputPerHour) || !Positive(x.MaximumLaborShare) || x.MaximumLaborShare > 1 || x.Inputs.Values.Any(n => !Positive(n))) ||
            Buildings.Any(x => !Positive(x.LaborHours) || x.Materials.Values.Any(n => !Positive(n)) ||
                !double.IsFinite(x.StorageCapacity) || x.StorageCapacity < 0 || x.Storage is { } storage && !storage.Valid() ||
                x.Technology is { Length: 0 } || x.Site is not (null or "ordinary" or "river" or "wind")) ||
            Discoveries.Any(x => !Positive(x.PracticeHours)))
            throw new InvalidOperationException("Некорректный каталог деятельности или ресурсов поселения");
        foreach (var id in new[] { "house", "well" }) if (!Buildings.Any(x => x.Id == id)) throw new InvalidOperationException($"Нет нормы {id}");
        foreach (var id in new[] { "forage", "game", "fiber", "timber", "fish", "clay", "stone", "iron_ore" })
            if (!NaturalPools.Any(x => x.Id == id)) throw new InvalidOperationException($"Нет природного пула {id}");
        foreach (var id in new[] { "water", "fiber", "cloth" })
            if (!Resources.Any(x => x.Id == id)) throw new InvalidOperationException($"Нет бытового ресурса {id}");
        if (Resources.Single(x => x.Id == "water").HouseholdNeed is not { PerPersonPerDay: > 0 })
            throw new InvalidOperationException("Нужна положительная норма питьевой воды");
        foreach (var activity in Activities)
            if (activity.Pool is { } pool && !NaturalPools.Any(x => x.Id == pool) ||
                activity.Discovery is { } discovery && !Discoveries.Any(x => x.Id == discovery))
                throw new InvalidOperationException($"Неизвестная ссылка в {activity.Id}");
        if (Primitive is { } primitive)
        {
            var technologyIds = primitive.Technologies.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (primitive.Processes.Any(p => !technologyIds.Contains(p.Technology)) ||
                primitive.Processes.Any(p => p.BuildingRequirements.Any(id => !Buildings.Any(b => b.Id == id))) ||
                Buildings.Any(b => b.Technology is { } technology && !technologyIds.Contains(technology)) ||
                primitive.Biosphere?.Animals.SelectMany(a => a.ProductRules).Any(p =>
                    p.Technology is { } technology && !technologyIds.Contains(technology)) == true)
                throw new InvalidOperationException("Неизвестная технология или ресурс поселкового процесса");
        }
    }
}

public sealed record NaturalPoolRule(string Id, bool Renewable, double Capacity, double RecoveryPerDay);
public sealed record HouseholdActivityRule(string Id, string Name, string Output, double OutputPerHour,
    double MaximumLaborShare, string? Pool, string? Discovery, IReadOnlyDictionary<string, double> Inputs);
public sealed record SettlementBuildingRule(string Id, double LaborHours, IReadOnlyDictionary<string, double> Materials,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double StorageCapacity = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BuildingStorageProfile? Storage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Technology = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Site = null);
public sealed record SettlementDiscoveryRule(string Id, string Name, string Practice, double PracticeHours);

public sealed record BuildingStorageProfile(double DecayMultiplier, double FallbackDecayMultiplier, string[] Preferred)
{
    public bool Valid() => double.IsFinite(DecayMultiplier) && DecayMultiplier > 0 && DecayMultiplier <= 1 &&
        double.IsFinite(FallbackDecayMultiplier) && FallbackDecayMultiplier > 0 && FallbackDecayMultiplier <= 1 &&
        Preferred.Length > 0 && Preferred.All(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record SettlementStorageRules
{
    public double OutdoorDecayMultiplier { get; init; } = 3;
    public double GeneralBuildingDecayMultiplier { get; init; } = .75;
    public Dictionary<string, double> VolumePerUnit { get; init; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        if (!double.IsFinite(OutdoorDecayMultiplier) || OutdoorDecayMultiplier <= 1 || OutdoorDecayMultiplier > 20 ||
            !double.IsFinite(GeneralBuildingDecayMultiplier) || GeneralBuildingDecayMultiplier <= 0 || GeneralBuildingDecayMultiplier > 1 ||
            VolumePerUnit.Count == 0 || VolumePerUnit.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value) || pair.Value <= 0))
            throw new InvalidOperationException("Некорректные нормы хранения поселения");
    }
    public double Volume(string resourceId, string category) =>
        VolumePerUnit.GetValueOrDefault("resource:" + resourceId, VolumePerUnit.GetValueOrDefault(category, 1));
}

public sealed record SettlementTrailRules
{
    public double WeakHalfLifeDays { get; init; } = 45;
    public double StrongHalfLifeDays { get; init; } = 240;
    public double TrafficForStrongTrail { get; init; } = 240;
    public double ForgetBelow { get; init; } = .003;
    public double MaximumCostReduction { get; init; } = .55;
    public void Validate()
    {
        if (!double.IsFinite(WeakHalfLifeDays) || WeakHalfLifeDays <= 0 || !double.IsFinite(StrongHalfLifeDays) || StrongHalfLifeDays < WeakHalfLifeDays ||
            !double.IsFinite(TrafficForStrongTrail) || TrafficForStrongTrail <= 0 || !double.IsFinite(ForgetBelow) || ForgetBelow <= 0 || ForgetBelow >= .025 ||
            !double.IsFinite(MaximumCostReduction) || MaximumCostReduction < 0 || MaximumCostReduction >= 1)
            throw new InvalidOperationException("Некорректные нормы износа троп");
    }
    public double Decay(double strength, double moisture, double forest)
    {
        var halfLife = WeakHalfLifeDays + (StrongHalfLifeDays - WeakHalfLifeDays) * strength * strength;
        var regrowth = .6 + Math.Clamp(moisture, 0, 1) * .8 + Math.Clamp(forest, 0, 1) * .5;
        return strength * Math.Pow(.5, regrowth / halfLife);
    }
}

public sealed record SettlementExplorationRules
{
    public int WindowDays { get; init; } = 14;
    public int PressureDays { get; init; } = 7;
    public double SupplyRadiusCost { get; init; } = 12;
    public double LaborPressureShare { get; init; } = .65;
    public double MinimumRenewalCoverage { get; init; } = 1.15;
    public int CooldownDays { get; init; } = 60;
    public int PartySize { get; init; } = 2;
    public double MaximumLaborShare { get; init; } = .1;
    public double HomeReserveDays { get; init; } = 2;
    public int MinimumProvisionDays { get; init; } = 4;
    public int MaximumProvisionDays { get; init; } = 14;
    public double BaseCarryTonnesPerPerson { get; init; } = .04;
    public double PackAnimalCapacityMultiplier { get; init; } = 2.5;
    public double RidingSpeedMultiplier { get; init; } = 1.65;
    public double RaftSpeedMultiplier { get; init; } = 1.35;
    public double RaftTimberTonnes { get; init; } = .025;
    public int MaximumExtensionDays { get; init; } = 14;
    public double ResupplyShare { get; init; } = .35;
    public double BaseLiveCaptureChance { get; init; } = .05;
    public double TamingCaptureMultiplier { get; init; } = 10;
    public double BaseFatalityChancePerDay { get; init; } = .0005;
    public double DecisionComplexity { get; init; } = 1.4;
    public int StepsPerDay { get; init; } = 24;
    public double SurveyHoursPerCell { get; init; } = .15;
    public int MaximumReports { get; init; } = 6;
    public void Validate()
    {
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        if (WindowDays is < 1 or > 90 || PressureDays < 1 || PressureDays > WindowDays || !Positive(SupplyRadiusCost) ||
            !Positive(LaborPressureShare) || LaborPressureShare > 1 || !Positive(MinimumRenewalCoverage) ||
            CooldownDays < 60 || PartySize < 1 || !Positive(MaximumLaborShare) || MaximumLaborShare > .25 ||
            !Positive(HomeReserveDays) || MinimumProvisionDays is < 4 or > 14 || MaximumProvisionDays < MinimumProvisionDays || MaximumProvisionDays > 14 ||
            !Positive(BaseCarryTonnesPerPerson) || !Positive(PackAnimalCapacityMultiplier) || PackAnimalCapacityMultiplier < 1 ||
            !Positive(RidingSpeedMultiplier) || RidingSpeedMultiplier < 1 || !Positive(RaftSpeedMultiplier) || RaftSpeedMultiplier < 1 ||
            !Positive(RaftTimberTonnes) || MaximumExtensionDays is < 0 or > 30 || !Positive(ResupplyShare) || ResupplyShare > 1 ||
            !Positive(BaseLiveCaptureChance) || BaseLiveCaptureChance > .25 || !Positive(TamingCaptureMultiplier) ||
            !Positive(BaseFatalityChancePerDay) || BaseFatalityChancePerDay > .05 || !Positive(DecisionComplexity) ||
            StepsPerDay is < 1 or > 64 || !Positive(SurveyHoursPerCell) || MaximumReports is < 1 or > 20)
            throw new InvalidOperationException("Некорректные нормы снабжения или разведки");
    }
}
