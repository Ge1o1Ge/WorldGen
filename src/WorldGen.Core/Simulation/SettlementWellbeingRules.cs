namespace WorldGen.Core.Simulation;

// Expectations are scenario parameters, not a universal psychological model.
public sealed record SettlementWellbeingRules
{
    public required IReadOnlyList<DietCategoryRule> Foods { get; init; }
    public double TastingMinimumShare { get; init; } = .02;
    public double FamiliarityPerMeal { get; init; } = .04;
    public double ExpectationRisePerDay { get; init; } = .015;
    public double ExpectationFallPerDay { get; init; } = .002;
    public double MaximumFoodExpectation { get; init; } = .2;
    public double PreferenceReserveShare { get; init; } = .2;
    public double SafetyFoodDays { get; init; } = 2;
    public double VarietyPriorityBonus { get; init; } = 2;
    public double InitialHousingExpectation { get; init; } = .7;
    public double InitialRestExpectation { get; init; } = .25;
    public double MaximumRestExpectation { get; init; } = .5;
    public double WaterTravelHoursPerPerson { get; init; } = .5;
    public double SatisfactionSmoothing { get; init; } = .08;
    public double ProjectPriorityBonus { get; init; } = 3;
    public double HousingImprovementThreshold { get; init; } = .15;
    public void Validate()
    {
        static bool Fraction(double n) => double.IsFinite(n) && n > 0 && n < 1;
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        if (Foods.Count is < 1 or > 8 || Foods.Select(f => f.Id).Distinct().Count() != Foods.Count ||
            Foods.Any(f => string.IsNullOrWhiteSpace(f.Id) || f.Id == "unknown" || string.IsNullOrWhiteSpace(f.Name) || f.Activities.Count == 0) ||
            Foods.SelectMany(f => f.Activities).Distinct().Count() != Foods.Sum(f => f.Activities.Count) ||
            !Fraction(TastingMinimumShare) || !Fraction(FamiliarityPerMeal) || !Fraction(ExpectationRisePerDay) ||
            !Fraction(ExpectationFallPerDay) || ExpectationFallPerDay > ExpectationRisePerDay ||
            !Fraction(MaximumFoodExpectation) || MaximumFoodExpectation * Foods.Count > 1 || !Fraction(PreferenceReserveShare) ||
            !Positive(SafetyFoodDays) || !Positive(VarietyPriorityBonus) || !Fraction(InitialHousingExpectation) ||
            !Fraction(InitialRestExpectation) || !Fraction(MaximumRestExpectation) || InitialRestExpectation > MaximumRestExpectation ||
            !Positive(WaterTravelHoursPerPerson) || !Fraction(SatisfactionSmoothing) || !Positive(ProjectPriorityBonus) || !Fraction(HousingImprovementThreshold))
            throw new InvalidOperationException("Некорректные нормы потребностей и ожиданий");
    }
}
public sealed record DietCategoryRule(string Id, string Name, IReadOnlyList<string> Activities);

// This is a partition of Stocks[food], never another inventory of edible mass.
public sealed class FoodComposition
{
    public Dictionary<string, double> Amounts { get; set; } = new(StringComparer.Ordinal);
    public void Reconcile(double total)
    {
        if (!double.IsFinite(total) || total < -1e-8 || Amounts.Values.Any(n => !double.IsFinite(n) || n < 0))
            throw new InvalidOperationException("Некорректный состав пищевого запаса");
        total = Math.Max(0, total);
        var sum = Amounts.Values.Sum();
        if (!double.IsFinite(sum)) throw new InvalidOperationException("Переполнение пищевого запаса");
        if (total == 0) { Amounts.Clear(); return; }
        // Do not rewrite a saved composition for floating-point summation noise.
        // Reconciliation must be idempotent, especially in expedition bags.
        if (Math.Abs(sum - total) <= Math.Max(1, total) * 1e-12) return;
        if (sum > total) Take(sum - total);
        else if (sum < total) Add("unknown", total - sum);
    }
    public void Add(string category, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > 0) Amounts[category] = Amounts.GetValueOrDefault(category) + amount;
    }
    public Dictionary<string, double> Take(double requested)
    {
        if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        var sum = Amounts.Values.Sum(); var remaining = Math.Min(requested, sum);
        var fraction = sum > 0 ? remaining / sum : 0;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var keys = Amounts.Keys.Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i]; var taken = Math.Min(Amounts[key], i == keys.Length - 1 ? remaining : Amounts[key] * fraction);
            result[key] = taken; Amounts[key] -= taken; remaining = Math.Max(0, remaining - taken);
            if (Amounts[key] == 0) Amounts.Remove(key);
        }
        return result;
    }
}

public sealed class SettlementWellbeingState
{
    public int StartedDay { get; init; }
    public int LastEvaluatedDay { get; set; } = -1;
    public FoodComposition FoodStock { get; set; } = new();
    public Dictionary<string, double> ConsumedToday { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HouseholdWellbeingState> Households { get; set; } = new(StringComparer.Ordinal);
    public double Satisfaction { get; set; } = 1;
    public Dictionary<string, double> Needs { get; set; } = new(StringComparer.Ordinal);
    public string MainConcern { get; set; } = "Ожидание первого наблюдения";
}

public sealed class FoodExperienceState
{
    public int? FirstTastedDay { get; set; }
    public double Familiarity { get; set; }
    public double ExpectedShare { get; set; }
    public double EatenShareToday { get; set; }
}

public sealed class HouseholdWellbeingState
{
    public int Members { get; set; }
    public int ObservedDays { get; set; }
    public Dictionary<string, FoodExperienceState> Foods { get; set; } = new(StringComparer.Ordinal);
    public double ExpectedHousing { get; set; }
    public double ExpectedRest { get; set; }
    public double WorkHours { get; set; }
    public double WorkCapacityHours { get; set; }
    public double FreeTimeShare { get; set; }
    public double WaterTravelHours { get; set; }
    public double HousingQuality { get; set; }
    public double Satisfaction { get; set; } = 1;
    public Dictionary<string, double> Needs { get; set; } = new(StringComparer.Ordinal);

    public void Observe(SettlementWellbeingRules rules, int day, double foodCoverage, double waterCoverage, IReadOnlyDictionary<string, double> eatenShares)
    {
        foreach (var category in rules.Foods)
        {
            if (!Foods.TryGetValue(category.Id, out var memory)) Foods[category.Id] = memory = new();
            var share = Math.Clamp(eatenShares.GetValueOrDefault(category.Id), 0, 1);
            memory.EatenShareToday = share;
            if (share * foodCoverage >= rules.TastingMinimumShare)
            {
                memory.FirstTastedDay ??= day;
                memory.Familiarity += (1 - memory.Familiarity) * rules.FamiliarityPerMeal;
            }
            // A trace or an unknown legacy meal does not establish an expectation.
            var experienced = memory.FirstTastedDay is not null ? Math.Min(rules.MaximumFoodExpectation, share * foodCoverage) * memory.Familiarity : 0;
            memory.ExpectedShare += (experienced - memory.ExpectedShare) *
                (experienced > memory.ExpectedShare ? rules.ExpectationRisePerDay : rules.ExpectationFallPerDay);
        }
        FreeTimeShare = WorkCapacityHours > 0 ? Math.Clamp(1 - WorkHours / WorkCapacityHours, 0, 1) : 0;
        ExpectedHousing += (HousingQuality - ExpectedHousing) * (HousingQuality > ExpectedHousing ? rules.ExpectationRisePerDay : rules.ExpectationFallPerDay);
        ExpectedHousing = Math.Clamp(ExpectedHousing, rules.InitialHousingExpectation, 1);
        var experiencedRest = Math.Clamp(FreeTimeShare, rules.InitialRestExpectation, rules.MaximumRestExpectation);
        ExpectedRest += (experiencedRest - ExpectedRest) * (experiencedRest > ExpectedRest ? rules.ExpectationRisePerDay : rules.ExpectationFallPerDay);
        Needs["food"] = 1 - Math.Clamp(foodCoverage, 0, 1);
        Needs["water"] = Math.Clamp(1 - waterCoverage + WaterTravelHours / Math.Max(1, Members) / rules.WaterTravelHoursPerPerson * .5, 0, 1);
        Needs["housing"] = Math.Clamp((ExpectedHousing - HousingQuality) / Math.Max(.01, ExpectedHousing), 0, 1);
        Needs["variety"] = Math.Clamp(Foods.Values.Sum(m => Math.Max(0, m.ExpectedShare - m.EatenShareToday)), 0, 1);
        Needs["rest"] = WorkCapacityHours > 0 ? Math.Clamp(1 - FreeTimeShare / Math.Max(.01, ExpectedRest), 0, 1) : 0;
        var raw = 1 - (.35 * Needs["food"] + .2 * Needs["water"] + .15 * Needs["housing"] + .15 * Needs["variety"] + .15 * Needs["rest"]);
        raw = Math.Min(raw, .25 + .75 * Math.Min(foodCoverage, waterCoverage));
        Satisfaction = ObservedDays == 0 ? raw : Satisfaction + (raw - Satisfaction) * rules.SatisfactionSmoothing;
        ObservedDays++;
    }
}
