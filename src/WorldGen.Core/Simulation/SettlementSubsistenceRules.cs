namespace WorldGen.Core.Simulation;

// Experimental one-hectare food ecology, not historical productivity estimates.
public sealed record SettlementSubsistenceRules
{
    public Dictionary<string, double> RecoveryMultipliers { get; init; } = new() { ["forage"] = .5, ["game"] = .5, ["fish"] = .25 };
    public Dictionary<string, double> PrimitiveEfficiency { get; init; } = new() { ["forage"] = .75, ["game"] = .4, ["fish"] = .25 };
    public Dictionary<string, double> EasyCatchTonnes { get; init; } = new() { ["forage"] = .08, ["game"] = .04, ["fish"] = 1 };
    public Dictionary<string, double> PressureHalfLifeDays { get; init; } = new() { ["forage"] = 90, ["game"] = 180, ["fish"] = 365 };
    public double EncounterExponent { get; init; } = .7;
    public double GardenLaborHours { get; init; } = 160;
    public double GardenTimber { get; init; } = .08;
    public double ClearingTonnesPerHour { get; init; } = .08;
    public int GardenGrowingDays { get; init; } = 30;
    public double GardenOutputPerHour { get; init; } = .004;
    public double GardenDailyYield { get; init; } = .03;
    public double GardenMaximumFoodShare { get; init; } = .9;
    public double FoodLaborPressure { get; init; } = .28;
    public int MoversPerDay { get; init; } = 3;
    public double MovingHoursPerPerson { get; init; } = 2;
    public WildlifeRules Wildlife { get; init; } = new();
    public double RecoveryScale(string pool) => RecoveryMultipliers.GetValueOrDefault(pool, 1);
    public void Validate()
    {
        Wildlife.Validate();
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        var maps = new[] { RecoveryMultipliers, PrimitiveEfficiency, EasyCatchTonnes, PressureHalfLifeDays };
        if (maps.Any(m => m.Count != 3 || new[] { "forage", "game", "fish" }.Any(k => !m.ContainsKey(k)) || m.Values.Any(n => !Positive(n))) ||
            RecoveryMultipliers.Values.Concat(PrimitiveEfficiency.Values).Any(n => n > 1) || !Positive(ClearingTonnesPerHour) ||
            !Positive(EncounterExponent) || !Positive(GardenLaborHours) || !Positive(GardenTimber) ||
            GardenGrowingDays is < 1 or > 365 || !Positive(GardenOutputPerHour) || !Positive(GardenDailyYield) ||
            !Positive(GardenMaximumFoodShare) || GardenMaximumFoodShare > 1 || !Positive(FoodLaborPressure) || FoodLaborPressure > 1 ||
            MoversPerDay < 1 || !Positive(MovingHoursPerPerson))
            throw new InvalidOperationException("Некорректные нормы истощения пищи, огородов или переезда");
    }
}

public sealed record WildlifeRules
{
    public int SeedPatchSize { get; init; } = 8;
    public int RangeRadiusCells { get; init; } = 3;
    public int QuietMoveIntervalDays { get; init; } = 14;
    public double AlertHalfLifeDays { get; init; } = 7;
    public double FleeThreshold { get; init; } = .05;
    public void Validate()
    {
        if (SeedPatchSize is < 2 or > 32 || RangeRadiusCells is < 1 or > 6 || QuietMoveIntervalDays < 1 ||
            !double.IsFinite(AlertHalfLifeDays) || AlertHalfLifeDays <= 0 || !double.IsFinite(FleeThreshold) || FleeThreshold <= 0)
            throw new InvalidOperationException("Некорректные нормы подвижных групп дичи");
    }
}
