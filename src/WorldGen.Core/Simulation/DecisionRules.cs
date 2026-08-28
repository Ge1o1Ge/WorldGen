namespace WorldGen.Core.Simulation;

public sealed record DecisionRules
{
    public double DailyPointsPerPerson { get; init; } = 1;
    public double DecayPerDay { get; init; } = .01;
    public double ExpertiseBonus { get; init; } = 1;
    public double ReputationMinimum { get; init; } = .5;
    public double ReputationMaximum { get; init; } = 1.5;
    public double ReputationStep { get; init; } = .04;
    public double MinimumEvidenceConfidence { get; init; } = .7;
    public double LearningHoursPerPerson { get; init; } = 40;
    public double ApprovalDays { get; init; } = 1.5;
    public double SiteDays { get; init; } = .35;
    public double Quorum { get; init; } = .5;
    public double SiteMajority { get; init; } = .6;
    public double SiteMargin { get; init; } = .1;
    public int MinimumDiscussionDays { get; init; } = 2;
    public int SiteStableDays { get; init; } = 2;
    public int ExpiryDays { get; init; } = 90;
    public int EvaluationDays { get; init; } = 30;
    public int MaximumEvaluationDays { get; init; } = 120;
    public int RetainedResults { get; init; } = 12;
    public Dictionary<string, double> Complexity { get; init; } = new(StringComparer.Ordinal)
        { ["house"] = 1, ["well"] = 1.5, ["relocation"] = 1 };
    public void Validate()
    {
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        if (!Positive(DailyPointsPerPerson) || !Positive(DecayPerDay) || DecayPerDay >= 1 ||
            !double.IsFinite(ExpertiseBonus) || ExpertiseBonus < 0 || ExpertiseBonus > 2 ||
            !Positive(ReputationMinimum) || ReputationMinimum > 1 || !Positive(ReputationMaximum) || ReputationMaximum < 1 || ReputationMaximum > 3 ||
            !Positive(ReputationStep) || ReputationStep > .1 || !Positive(MinimumEvidenceConfidence) || MinimumEvidenceConfidence > 1 ||
            !Positive(LearningHoursPerPerson) || !Positive(ApprovalDays) || !Positive(SiteDays) ||
            !Positive(Quorum) || Quorum > 1 || !Positive(SiteMajority) || SiteMajority <= .5 || SiteMajority > 1 ||
            !Positive(SiteMargin) || SiteMargin > 1 || MinimumDiscussionDays is < 1 or > 3650 || SiteStableDays is < 1 or > 3650 ||
            ExpiryDays < MinimumDiscussionDays + SiteStableDays || ExpiryDays > 3650 || EvaluationDays < 1 ||
            MaximumEvaluationDays < EvaluationDays || MaximumEvaluationDays > 3650 || RetainedResults is < 1 or > 100)
            throw new InvalidOperationException("Некорректные нормы коллективных решений");
        if (new[] { "house", "well", "relocation" }.Any(id => !Complexity.TryGetValue(id, out var value) || !Positive(value) || value > 50))
            throw new InvalidOperationException("Нужна конечная положительная сложность строительных решений");
    }
}
