using WorldGen.Core.Simulation;

namespace WorldGen.Tests;

public sealed class CollectiveDecisionTests
{
    private static readonly DecisionRules Rules = new();
    private static CollectiveProposal Proposal(string id = "project", string scope = "city", string domain = "construction", double threshold = 10000) =>
        new() { Id = id, Key = id, Scope = scope, Domain = domain, Kind = "house", Reason = "Нужно жильё", CreatedDay = 1,
            RequiredSupport = threshold, RequiredSiteSupport = 2, Sites = [new() { Id = "a" }, new() { Id = "b" }] };
    private static DecisionElector Voter(string id = "resident", int members = 1, string scope = "city", string[]? known = null) =>
        new(id, scope, members, (known ?? ["project"]).ToHashSet(), new[] { "a", "b" }.ToHashSet());

    [Fact]
    public void OnePointWithOnePercentDecayApproachesButNeverReachesOneHundred()
    {
        var p = Proposal(); var state = new CollectiveDecisionState { Proposals = [p] };
        var rules = Rules with { ExpiryDays = 3650 };
        for (var day = 1; day <= 100; day++) CollectiveDecisions.Advance(state, rules, day, [Voter()], [new("resident", p.Id, null, 1)]);
        Assert.InRange(p.Support, 63.3967, 63.3968);
        var previous = p.Support;
        CollectiveDecisions.Advance(state, rules, 101, [Voter()], []);
        Assert.Equal(previous * .99, p.Support, 10);
        Assert.Equal(0, state.SpentToday); Assert.Equal(1, state.IssuedToday);
    }

    [Fact]
    public void AttentionCannotBeSpentTwiceAndInvalidInputDoesNotMutateState()
    {
        var p = Proposal(); var state = new CollectiveDecisionState { Proposals = [p] };
        Assert.Throws<InvalidOperationException>(() => CollectiveDecisions.Advance(state, Rules, 1, [Voter(), Voter("chief")],
            [new("resident", p.Id, null, 1)], [new("resident", "chief", .5)]));
        Assert.Equal(-1, state.LastDay); Assert.Equal(0, p.Support);
        CollectiveDecisions.Advance(state, Rules, 1, [Voter()], [new("resident", p.Id, null, 1)]);
        Assert.Throws<InvalidOperationException>(() => CollectiveDecisions.Advance(state, Rules, 1, [Voter()], [new("resident", p.Id, null, 1)]));
        Assert.Equal(1, p.Support);
    }

    [Fact]
    public void ExpertiseIsDomainSpecificAndDelegationAmplifiesOnlyOnce()
    {
        var p = Proposal(); var water = Proposal("water", domain: "water");
        var state = new CollectiveDecisionState { Proposals = [p, water] };
        state.Profiles["chief"] = new() { Members = 1, PracticeHours = new() { ["construction"] = 10000 } };
        var voters = new[] { Voter(known: ["project", "water"]), Voter("chief", known: ["project", "water"]) };
        CollectiveDecisions.Advance(state, Rules, 1, voters,
            [new("chief", p.Id, null, .5), new("chief", water.Id, null, .5)], [new("resident", "chief", 1)]);
        Assert.Equal(2, state.SpentToday); Assert.Equal(1, state.DelegatedToday);
        Assert.Equal(2, p.Support); Assert.Equal(1, water.Support);
        Assert.Contains(p.Backers, b => b.SourceId == "resident" && b.DeciderId == "chief");
        Assert.Throws<InvalidOperationException>(() => CollectiveDecisions.Advance(state, Rules, 2, voters, [],
            [new("resident", "chief", 1), new("chief", "resident", 1)]));
        Assert.Equal(1, state.LastDay);
    }

    [Fact]
    public void DelegationDoesNotLaunderTheSourcesReputationOrRecursivelyForwardVotes()
    {
        var p = Proposal(); var state = new CollectiveDecisionState { Proposals = [p] };
        state.Profiles["a"] = new() { Reputation = new() { ["construction"] = .5 } };
        var voters = new[] { Voter("a"), Voter("b"), Voter("c") };
        CollectiveDecisions.Advance(state, Rules, 1, voters, [new("b", p.Id, null, .5), new("c", p.Id, null, 1)],
            [new("a", "b", 1), new("b", "c", .5)]);
        Assert.Equal(3, state.SpentToday); Assert.Equal(2.5, p.Support);
        Assert.DoesNotContain(p.Backers, b => b.SourceId == "a" && b.DeciderId == "c");
    }

    [Fact]
    public void UnknownProjectsSitesAndForeignAuthorityCannotReceiveSupport()
    {
        var p = Proposal(); var state = new CollectiveDecisionState { Proposals = [p] };
        CollectiveDecisions.Advance(state, Rules, 1, [Voter(known: []), Voter("outsider", scope: "other")],
            [new("resident", p.Id, null, 1), new("outsider", p.Id, null, 1)]);
        Assert.Equal(0, p.Support); Assert.Equal(0, state.SpentToday);
        p.Phase = "site";
        var blind = Voter() with { KnownSites = new HashSet<string>() };
        CollectiveDecisions.Advance(state, Rules, 2, [blind], [new("resident", p.Id, "a", 1)]);
        Assert.All(p.Sites, site => Assert.Equal(0, site.Support));
    }

    [Fact]
    public void IdeaAndSiteUseDifferentDaysAndTiedSitesNeverWinById()
    {
        var p = Proposal(threshold: 3); var state = new CollectiveDecisionState { Proposals = [p] };
        var voters = new[] { Voter("a"), Voter("b") };
        for (var day = 1; day <= 2; day++) CollectiveDecisions.Advance(state, Rules, day, voters, [new("a", p.Id, null, 1), new("b", p.Id, null, 1)]);
        Assert.Equal("site", p.Phase); Assert.All(p.Sites, s => Assert.Equal(0, s.Support));
        for (var day = 3; day <= 6; day++) CollectiveDecisions.Advance(state, Rules, day, voters, [new("a", p.Id, "a", 1), new("b", p.Id, "b", 1)]);
        Assert.Equal("site", p.Phase); Assert.Null(p.SelectedSite);
        for (var day = 7; day <= 10; day++) CollectiveDecisions.Advance(state, Rules, day, voters, [new("a", p.Id, "b", 1), new("b", p.Id, "b", 1)]);
        Assert.Equal("approved", p.Phase); Assert.Equal("b", p.SelectedSite);
        Assert.Null(p.BuildingId); // Mandates do not create physical resources.
    }

    [Fact]
    public void AnExpertCannotReplaceThePopulationQuorum()
    {
        var p = Proposal(threshold: 1); var state = new CollectiveDecisionState { Proposals = [p] };
        state.Profiles["expert"] = new() { PracticeHours = new() { ["construction"] = 10000 } };
        for (var day = 1; day < 5; day++) CollectiveDecisions.Advance(state, Rules, day, [Voter("expert"), Voter("others", members: 20)], [new("expert", p.Id, null, 1)]);
        Assert.True(p.Support > p.RequiredSupport); Assert.Equal("idea", p.Phase);
    }

    [Fact]
    public void ReputationRequiresObservedOutcomeIsBoundedAndAssessedOnlyOnce()
    {
        var p = Proposal(); p.Phase = "observing"; p.FinishedDay = 1; p.SelectedSite = "a"; p.ObservedDays = 30;
        p.Backers.Add(new() { SourceId = "resident", DeciderId = "resident", Points = 10 });
        p.Sites[0].Backers.Add(new() { SourceId = "winner", DeciderId = "winner", Points = 10 });
        p.Sites[1].Backers.Add(new() { SourceId = "loser", DeciderId = "loser", Points = 10 });
        var state = new CollectiveDecisionState { Proposals = [p], Profiles = new() { ["resident"] = new(), ["winner"] = new(), ["loser"] = new() } };
        Assert.False(CollectiveDecisions.Assess(state, p, Rules, 10, -1, 1, "Рано"));
        Assert.False(CollectiveDecisions.Assess(state, p, Rules, 31, -1, .2, "Внешний кризис"));
        Assert.True(CollectiveDecisions.Assess(state, p, Rules, 31, -1, 1, "Пользы нет"));
        Assert.Equal(.96, state.Profiles["resident"].Reputation["construction"], 10);
        Assert.Equal(.96, state.Profiles["winner"].Reputation["construction"], 10);
        Assert.Empty(state.Profiles["loser"].Reputation);
        Assert.False(CollectiveDecisions.Assess(state, p, Rules, 32, 1, 1, "Повтор"));
        Assert.Equal(.96, state.Profiles["resident"].Reputation["construction"], 10);
        state.Profiles["resident"].Reputation["construction"] = .51;
        p.Phase = "observing"; p.AssessedDay = null;
        Assert.True(CollectiveDecisions.Assess(state, p, Rules, 33, -1, 1, "Другой результат"));
        Assert.Equal(.5, state.Profiles["resident"].Reputation["construction"]);
    }

    [Fact]
    public void APlacementFailureDoesNotPenalizeIdeaOnlyOrLosingSiteSupporters()
    {
        var p = Proposal(); p.Phase = "observing"; p.FinishedDay = 1; p.SelectedSite = "a"; p.ObservedDays = 30;
        p.Backers.Add(new() { SourceId = "idea", DeciderId = "idea", Points = 10 });
        p.Sites[0].Backers.Add(new() { SourceId = "winner", DeciderId = "winner", Points = 10 });
        p.Sites[1].Backers.Add(new() { SourceId = "loser", DeciderId = "loser", Points = 10 });
        var state = new CollectiveDecisionState { Proposals = [p], Profiles = new() { ["idea"] = new(), ["winner"] = new(), ["loser"] = new() } };
        Assert.True(CollectiveDecisions.Assess(state, p, Rules, 31, -1, 1, "Доказана ошибка размещения", ideaBenefit: 0, siteBenefit: -1));
        Assert.Equal(1, state.Profiles["idea"].Reputation.GetValueOrDefault("construction", 1));
        Assert.Equal(.96, state.Profiles["winner"].Reputation["construction"], 10);
        Assert.Empty(state.Profiles["loser"].Reputation);
    }

    [Fact]
    public void APartlyUsefulResultGivesOnlyAPartialRewardEvenAfterManyVotes()
    {
        var p = Proposal(); p.Phase = "observing"; p.FinishedDay = 1; p.SelectedSite = "a"; p.ObservedDays = 30;
        p.Backers.Add(new() { SourceId = "resident", DeciderId = "resident", Points = 10000 });
        var state = new CollectiveDecisionState { Proposals = [p], Profiles = new() { ["resident"] = new() } };
        Assert.True(CollectiveDecisions.Assess(state, p, Rules, 31, .5, 1, "Частичная польза"));
        Assert.Equal(1.02, state.Profiles["resident"].Reputation["construction"], 10);
    }

    [Fact]
    public void LostApprovedLocationRequiresANewSiteDecision()
    {
        var p = Proposal(); p.Phase = "approved"; p.ApprovedDay = 2; p.SelectedSite = "a"; p.Sites[0].Available = false;
        var state = new CollectiveDecisionState { Proposals = [p] };
        CollectiveDecisions.Advance(state, Rules, 3, [Voter()], []);
        Assert.Equal("site", p.Phase); Assert.Null(p.SelectedSite); Assert.Null(p.ApprovedDay);
        Assert.Throws<InvalidOperationException>(() => CollectiveDecisions.MarkStarted(p, "house", 3));
    }

    [Fact]
    public void LostNeedCancelsWithoutPunishmentAndRulesRejectUnboundedValues()
    {
        var p = Proposal(); p.Available = false; var state = new CollectiveDecisionState { Proposals = [p] };
        CollectiveDecisions.Advance(state, Rules, 1, [Voter()], [new("resident", p.Id, null, 1)]);
        Assert.Equal("cancelled", p.Phase); Assert.Equal(0, state.SpentToday);
        Assert.Empty(state.Profiles["resident"].Reputation);
        Assert.Throws<InvalidOperationException>(() => (Rules with { DecayPerDay = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Rules with { ReputationMaximum = 100 }).Validate());
    }
}
