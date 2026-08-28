using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed class CollectiveDecisionState
{
    public int LastDay { get; set; } = -1;
    public int NextId { get; set; } = 1;
    public double IssuedToday { get; set; }
    public double SpentToday { get; set; }
    public double WeightedToday { get; set; }
    public double DelegatedToday { get; set; }
    public Dictionary<string, DecisionProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
    public List<CollectiveProposal> Proposals { get; set; } = [];
}
public sealed class DecisionProfile
{
    public int Members { get; set; }
    public Dictionary<string, double> PracticeHours { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Reputation { get; set; } = new(StringComparer.Ordinal);
}
public sealed class CollectiveProposal
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Scope { get; init; }
    public required string Domain { get; init; }
    public required string Kind { get; init; }
    public required string Reason { get; init; }
    public int CreatedDay { get; init; }
    public double RequiredSupport { get; init; }
    public double RequiredSiteSupport { get; init; }
    public string Phase { get; set; } = "idea";
    public bool Available { get; set; } = true;
    public string? Replaces { get; init; }
    public List<DecisionBacking> Backers { get; set; } = [];
    public List<DecisionSite> Sites { get; init; } = [];
    public string? LeadingSite { get; set; }
    public int LeadingDays { get; set; }
    public string? SelectedSite { get; set; }
    public int? ApprovedDay { get; set; }
    public string? BuildingId { get; set; }
    public int? StartedDay { get; set; }
    public int? FinishedDay { get; set; }
    public int ObservedDays { get; set; }
    public double ObservedBenefit { get; set; }
    public int? AssessedDay { get; set; }
    public double? Outcome { get; set; }
    public double? IdeaOutcome { get; set; }
    public double? SiteOutcome { get; set; }
    public string? OutcomeNote { get; set; }
    public string? CauseEventId { get; set; }
    public double Support => Backers.Sum(b => b.Points);
}
public sealed class DecisionSite
{
    public required string Id { get; init; }
    public CellAddress Cell { get; init; }
    public bool Available { get; set; } = true;
    public List<DecisionBacking> Backers { get; set; } = [];
    public double Support => Backers.Sum(b => b.Points);
}
public sealed class DecisionBacking
{
    public required string SourceId { get; init; }
    public required string DeciderId { get; init; }
    public double Points { get; set; }
}
public sealed record DecisionElector(string Id, string Scope, int Members,
    IReadOnlySet<string> KnownProposals, IReadOnlySet<string> KnownSites);
public sealed record DecisionBallot(string VoterId, string ProposalId, string? SiteId, double Share);
public sealed record DecisionDelegation(string SourceId, string RepresentativeId, double Share);
