namespace WorldGen.Core.Simulation;

/// <summary>Attention is conserved; expertise changes weight, never creates a second
/// spendable budget. This component grants mandates, not resources or buildings.</summary>
public static class CollectiveDecisions
{
    public static bool Pending(CollectiveProposal p) => p.Phase is "idea" or "site" or "approved";

    public static void Advance(CollectiveDecisionState state, DecisionRules rules, int day,
        IReadOnlyList<DecisionElector> electors, IReadOnlyList<DecisionBallot> ballots,
        IReadOnlyList<DecisionDelegation>? delegations = null)
    {
        rules.Validate();
        if (day < 0 || day <= state.LastDay) throw new InvalidOperationException("Бюджет решений уже рассчитан для этого дня");
        delegations ??= [];
        var voters = electors.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var proposals = state.Proposals.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (electors.Any(v => v.Members < 0) || ballots.Any(b => !voters.ContainsKey(b.VoterId) || !ValidShare(b.Share)) ||
            delegations.Any(d => !voters.ContainsKey(d.SourceId) || !voters.ContainsKey(d.RepresentativeId) || !ValidShare(d.Share)))
            throw new InvalidOperationException("Некорректное распределение участия");
        foreach (var voter in electors)
            if (ballots.Where(b => b.VoterId == voter.Id).Sum(b => b.Share) + delegations.Where(d => d.SourceId == voter.Id).Sum(d => d.Share) > 1 + 1e-10)
                throw new InvalidOperationException("Один дневной бюджет нельзя потратить дважды");
        // Validate before changing any state. Even dormant delegation cycles are rejected.
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Циклическое делегирование запрещено");
            foreach (var d in delegations.Where(d => d.SourceId == id && d.Share > 0)) Visit(d.RepresentativeId);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var voter in electors) Visit(voter.Id);
        var elapsed = state.LastDay < 0 ? 1 : day - state.LastDay;
        state.LastDay = day; state.SpentToday = state.WeightedToday = state.DelegatedToday = 0;
        state.IssuedToday = electors.Sum(v => v.Members * rules.DailyPointsPerPerson);
        foreach (var voter in electors)
        {
            if (!state.Profiles.TryGetValue(voter.Id, out var profile)) state.Profiles[voter.Id] = profile = new();
            profile.Members = voter.Members;
        }
        foreach (var proposal in state.Proposals.Where(Pending))
        {
            if (!proposal.Available || day - proposal.CreatedDay >= rules.ExpiryDays)
            { proposal.Phase = "cancelled"; proposal.OutcomeNote = "Необходимость/возможность исчезла или истёк срок обсуждения"; continue; }
            if (proposal.Phase == "approved" && !proposal.Sites.Any(s => s.Id == proposal.SelectedSite && s.Available))
            {
                proposal.Phase = "site"; proposal.SelectedSite = proposal.LeadingSite = null;
                proposal.ApprovedDay = null; proposal.LeadingDays = 0;
            }
            if (proposal.Phase == "idea") Decay(proposal.Backers);
            if (proposal.Phase == "site") foreach (var site in proposal.Sites) Decay(site.Backers);
        }
        void Decay(List<DecisionBacking> accounts)
        {
            foreach (var account in accounts) account.Points *= Math.Pow(1 - rules.DecayPerDay, elapsed);
            accounts.RemoveAll(a => a.Points < 1e-9);
        }
        var rawSupport = new Dictionary<(string Proposal, string? Site), double>();
        void Cast(DecisionElector source, DecisionElector decider, DecisionBallot ballot, double raw, bool delegated)
        {
            if (raw <= 0 || !proposals.TryGetValue(ballot.ProposalId, out var p) || !p.Available ||
                p.Scope != source.Scope || p.Scope != decider.Scope ||
                !source.KnownProposals.Contains(p.Id) || !decider.KnownProposals.Contains(p.Id)) return;
            List<DecisionBacking> accounts;
            if (p.Phase == "idea" && ballot.SiteId is null) accounts = p.Backers;
            else if (p.Phase == "site" && p.Sites.FirstOrDefault(s => s.Id == ballot.SiteId && s.Available) is { } site &&
                source.KnownSites.Contains(site.Id) && decider.KnownSites.Contains(site.Id)) accounts = site.Backers;
            else return;
            var profile = state.Profiles[decider.Id];
            var competence = Math.Clamp(profile.PracticeHours.GetValueOrDefault(p.Domain) / Math.Max(1, profile.Members * rules.LearningHoursPerPerson), 0, 1);
            var reputation = state.Profiles[source.Id].Reputation.GetValueOrDefault(p.Domain, 1);
            var weight = raw * (1 + competence * rules.ExpertiseBonus) * Math.Clamp(reputation, rules.ReputationMinimum, rules.ReputationMaximum);
            var account = accounts.FirstOrDefault(a => a.SourceId == source.Id && a.DeciderId == decider.Id);
            if (account is null) { account = new() { SourceId = source.Id, DeciderId = decider.Id }; accounts.Add(account); }
            account.Points += weight;
            rawSupport[(p.Id, ballot.SiteId)] = rawSupport.GetValueOrDefault((p.Id, ballot.SiteId)) + raw;
            state.SpentToday += raw; state.WeightedToday += weight;
            if (delegated) state.DelegatedToday += raw;
        }
        var ordered = ballots.OrderBy(b => b.VoterId, StringComparer.Ordinal).ThenBy(b => b.ProposalId, StringComparer.Ordinal).ThenBy(b => b.SiteId, StringComparer.Ordinal).ThenBy(b => b.Share).ToArray();
        foreach (var ballot in ordered)
        {
            var voter = voters[ballot.VoterId];
            Cast(voter, voter, ballot, voter.Members * rules.DailyPointsPerPerson * ballot.Share, false);
        }
        foreach (var delegation in delegations.OrderBy(d => d.SourceId, StringComparer.Ordinal).ThenBy(d => d.RepresentativeId, StringComparer.Ordinal).ThenBy(d => d.Share))
        {
            var source = voters[delegation.SourceId]; var representative = voters[delegation.RepresentativeId];
            var preferences = ordered.Where(b => b.VoterId == representative.Id).ToArray(); var total = preferences.Sum(b => b.Share);
            if (total <= 0) continue;
            foreach (var preference in preferences)
                Cast(source, representative, preference, source.Members * rules.DailyPointsPerPerson * delegation.Share * preference.Share / total, true);
        }
        foreach (var proposal in state.Proposals.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var quorum = electors.Where(v => v.Scope == proposal.Scope).Sum(v => v.Members) * rules.DailyPointsPerPerson * rules.Quorum;
            if (quorum <= 0) continue;
            if (proposal.Phase == "idea" && day - proposal.CreatedDay + 1 >= rules.MinimumDiscussionDays &&
                proposal.Support >= proposal.RequiredSupport && rawSupport.GetValueOrDefault((proposal.Id, null)) >= quorum)
                proposal.Phase = "site"; // Today's attention cannot also vote on a site.
            else if (proposal.Phase == "site")
            {
                var sites = proposal.Sites.Where(s => s.Available).OrderByDescending(s => s.Support).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
                var total = sites.Sum(s => s.Support); var leader = sites.FirstOrDefault();
                var runner = sites.Skip(1).FirstOrDefault()?.Support ?? 0;
                var decisive = leader is not null && total > 0 && leader.Support >= proposal.RequiredSiteSupport &&
                    leader.Support / total >= rules.SiteMajority && (leader.Support - runner) / total >= rules.SiteMargin &&
                    rawSupport.GetValueOrDefault((proposal.Id, leader.Id)) >= quorum;
                if (!decisive) { proposal.LeadingSite = null; proposal.LeadingDays = 0; continue; }
                proposal.LeadingDays = proposal.LeadingSite == leader!.Id && elapsed == 1 ? proposal.LeadingDays + 1 : 1;
                proposal.LeadingSite = leader.Id;
                if (proposal.LeadingDays >= rules.SiteStableDays)
                { proposal.Phase = "approved"; proposal.SelectedSite = leader.Id; proposal.ApprovedDay = day; }
            }
        }
        var archived = state.Proposals.Where(p => p.Phase is "assessed" or "uncertain" or "cancelled")
            .OrderByDescending(p => p.CreatedDay).ThenByDescending(p => p.Id, StringComparer.Ordinal).Skip(rules.RetainedResults).ToHashSet();
        state.Proposals.RemoveAll(archived.Contains);
    }

    public static void MarkStarted(CollectiveProposal proposal, string buildingId, int day)
    {
        if (proposal.Phase != "approved" || proposal.SelectedSite is null || proposal.ApprovedDay is null || day < proposal.ApprovedDay)
            throw new InvalidOperationException("Нельзя исполнить неодобренное решение");
        proposal.BuildingId = buildingId; proposal.StartedDay = day; proposal.Phase = "executing";
    }

    public static bool Assess(CollectiveDecisionState state, CollectiveProposal proposal, DecisionRules rules, int day,
        double benefit, double confidence, string note, double? ideaBenefit = null, double? siteBenefit = null)
    {
        rules.Validate();
        static bool Score(double score) => double.IsFinite(score) && score >= -1 && score <= 1;
        if (!Score(benefit) || !Score(ideaBenefit ?? benefit) || !Score(siteBenefit ?? benefit) || !double.IsFinite(confidence) || confidence < 0 || confidence > 1)
            throw new ArgumentOutOfRangeException(nameof(benefit));
        if (proposal.Phase != "observing" || proposal.AssessedDay is not null || proposal.FinishedDay is null ||
            day - proposal.FinishedDay < rules.EvaluationDays || proposal.ObservedDays < rules.EvaluationDays || confidence < rules.MinimumEvidenceConfidence) return false;
        // Concept supporters share responsibility. Only backers of the chosen
        // location share placement responsibility; losing alternatives do not.
        var credit = new Dictionary<string, (double Weight, double Result)>(StringComparer.Ordinal);
        void AddCredit(string id, double weight, double score)
        {
            var previous = credit.GetValueOrDefault(id);
            credit[id] = (previous.Weight + weight, previous.Result + weight * score);
        }
        void Credit(IEnumerable<DecisionBacking> accounts, double fraction, double score)
        {
            foreach (var account in accounts)
            {
                var share = account.Points * fraction;
                if (account.SourceId == account.DeciderId) AddCredit(account.SourceId, share, score);
                else
                {
                    AddCredit(account.SourceId, share / 2, score);
                    AddCredit(account.DeciderId, share / 2, score);
                }
            }
        }
        Credit(proposal.Backers, .5, ideaBenefit ?? benefit);
        Credit(proposal.Sites.Single(s => s.Id == proposal.SelectedSite).Backers, .5, siteBenefit ?? benefit);
        foreach (var (id, amount) in credit.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!state.Profiles.TryGetValue(id, out var profile)) continue;
            var participation = Math.Min(1, amount.Weight / Math.Max(1, profile.Members * rules.DailyPointsPerPerson));
            var score = amount.Result / Math.Max(1e-9, amount.Weight);
            profile.Reputation[proposal.Domain] = Math.Clamp(profile.Reputation.GetValueOrDefault(proposal.Domain, 1) +
                rules.ReputationStep * score * confidence * participation, rules.ReputationMinimum, rules.ReputationMaximum);
        }
        proposal.AssessedDay = day; proposal.Outcome = benefit; proposal.OutcomeNote = note; proposal.Phase = "assessed";
        proposal.IdeaOutcome = ideaBenefit ?? benefit; proposal.SiteOutcome = siteBenefit ?? benefit;
        return true;
    }

    private static bool ValidShare(double share) => double.IsFinite(share) && share >= 0 && share <= 1;
}
