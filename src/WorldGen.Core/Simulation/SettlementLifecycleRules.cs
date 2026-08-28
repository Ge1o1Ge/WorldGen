namespace WorldGen.Core.Simulation;

// Scenario units, not historical durability estimates. Optional for old scenarios/replays.
public sealed record SettlementLifecycleRules
{
    public required IReadOnlyList<BuildingMaterialRule> Materials { get; init; }
    public double RepairTrigger { get; init; } = .03;
    public double MaintenanceLaborShare { get; init; } = .15;
    public double ReplacementAgeShare { get; init; } = .8;
    public double ReplacementEfficiency { get; init; } = .6;
    public double UnsafeEfficiency { get; init; } = .1;
    public double WellCapacity { get; init; } = 1.2;
    public double WellRechargePerDay { get; init; } = .9;
    public double SoilLossPerTonne { get; init; } = .025;
    public double FallowRecoveryPerDay { get; init; } = .001;
    public double MinimumFieldOutputPerHour { get; init; } = .0008;
    public int PoorHarvestDays { get; init; } = 14;
    public int MeadowRecoveryDays { get; init; } = 365;
    public double MasonryPracticeHours { get; init; } = 2000;
    public void Validate()
    {
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        static bool Fraction(double n) => Positive(n) && n < 1;
        if (Materials.Count == 0 || Materials.Select(m => m.Id).Distinct().Count() != Materials.Count ||
            !Materials.Any(m => m.Id == "wood") || !Fraction(RepairTrigger) || !Fraction(MaintenanceLaborShare) ||
            !Fraction(ReplacementAgeShare) || !Fraction(ReplacementEfficiency) || !Fraction(UnsafeEfficiency) ||
            UnsafeEfficiency >= ReplacementEfficiency || !Positive(WellCapacity) || !Positive(WellRechargePerDay) ||
            !Fraction(SoilLossPerTonne) || !Fraction(FallowRecoveryPerDay) || !Positive(MinimumFieldOutputPerHour) ||
            PoorHarvestDays < 1 || MeadowRecoveryDays < 1 || !Positive(MasonryPracticeHours))
            throw new InvalidOperationException("Некорректные нормы жизненного цикла поселения");
        foreach (var m in Materials)
            if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name) || m.GraceDays < 0 || m.ServiceLifeDays <= m.GraceDays ||
                !Positive(m.AnnualWear) || !Fraction(m.PermanentShare) || !Positive(m.LaborMultiplier) ||
                !Positive(m.RepairLaborPerWear) || !Positive(m.DemolitionHours) || !Fraction(m.SalvageShare) ||
                m.Materials.Count == 0 || m.Materials.Values.Any(n => !Positive(n)) || !Positive(m.RepairMaterialMultiplier) ||
                m.Discovery is not (null or "masonry"))
                throw new InvalidOperationException($"Некорректный строительный материал {m.Id}");
    }
}

public sealed record BuildingMaterialRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int GraceDays { get; init; }
    public required int ServiceLifeDays { get; init; }
    public required double AnnualWear { get; init; }
    public required double PermanentShare { get; init; }
    public required double LaborMultiplier { get; init; }
    public required IReadOnlyDictionary<string, double> Materials { get; init; }
    public double RepairLaborPerWear { get; init; } = 120;
    public double RepairMaterialMultiplier { get; init; } = 1;
    public double DemolitionHours { get; init; } = 24;
    public double SalvageShare { get; init; } = .2;
    public string? Discovery { get; init; }
}

public sealed class BuildingLifecycleState
{
    public required string Material { get; init; }
    public int AccountedFromDay { get; set; }
    public bool BaselineAssessment { get; init; }
    public int AgeDays { get; set; }
    public int LastAgedDay { get; set; }
    public double RepairableWear { get; set; }
    public double PermanentWear { get; set; }
    public double Efficiency => Math.Clamp(1 - RepairableWear - PermanentWear, 0, 1);
    public bool Retiring { get; set; }
    public double DemolitionDone { get; set; }
    public Dictionary<string, double> InvestedMaterials { get; set; } = new(StringComparer.Ordinal);

    public void Age(BuildingMaterialRule rule, int day)
    {
        var elapsed = Math.Max(0, day - LastAgedDay);
        var wearingDays = Math.Max(0, AgeDays + elapsed - rule.GraceDays) - Math.Max(0, AgeDays - rule.GraceDays);
        AgeDays += elapsed; LastAgedDay = Math.Max(LastAgedDay, day);
        PermanentWear = Math.Clamp(PermanentWear + wearingDays * rule.AnnualWear * rule.PermanentShare / 365, 0, 1);
        // Near the maximum service age, structural damage dominates regardless of repairs.
        var ageFloor = Math.Clamp((AgeDays / (double)rule.ServiceLifeDays - .9) / .1, 0, 1);
        PermanentWear = AgeDays >= rule.ServiceLifeDays ? 1 : Math.Max(PermanentWear, ageFloor);
        RepairableWear = Math.Clamp(RepairableWear + wearingDays * rule.AnnualWear * (1 - rule.PermanentShare) / 365, 0, 1 - PermanentWear);
    }
}

public sealed class WellStorageState
{
    public double Stock { get; set; }
    public double Capacity { get; set; }
    public double RechargeRate { get; set; }
    public double RechargedToday { get; set; }
    public double WithdrawnToday { get; set; }
    public double OverflowToday { get; set; }
    public int LastRechargeDay { get; set; } = -1;
}

public sealed class FieldLifecycleState
{
    public double Harvested { get; set; }
    public double ExpectedOutputPerHour { get; set; }
    public int PoorYieldDays { get; set; }
    public int? FallowSinceDay { get; set; }
}

public sealed class SettlementMaintenanceState
{
    public double RepairHours { get; set; }
    public double DemolitionHours { get; set; }
    public double RepairableWear { get; set; }
    public double PermanentWear { get; set; }
    public double MeanEfficiency { get; set; } = 1;
    public int ReplacementNeeded { get; set; }
    public int Demolished { get; set; }
    public int FallowFields { get; set; }
    public Dictionary<string, double> MaterialsUsed { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Salvaged { get; set; } = new(StringComparer.Ordinal);
}
