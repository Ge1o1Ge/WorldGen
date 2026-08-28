namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private HouseholdWellbeingState NewWellbeing() => new()
    { ExpectedHousing = Rules.Wellbeing!.InitialHousingExpectation, ExpectedRest = Rules.Wellbeing.InitialRestExpectation };

    private List<DwellingState> WellbeingHomes(CityState city)
    {
        var homes = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "house" && b.Status == "active" && b.Residents > 0).ToList();
        var unhoused = Math.Max(0, Population(city) - homes.Sum(h => h.Residents));
        if (unhoused > 0) homes.Add(new()
        {
            Id = $"camp:{city.Id}",
            CityId = city.Id,
            Kind = "camp",
            Residents = unhoused,
            Cell = addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!]
        });
        return homes;
    }

    private void InitializeWellbeing()
    {
        if (Rules.Wellbeing is null) return;
        foreach (var city in world.Cities.Values)
        {
            State.Cities[city.Id].Wellbeing ??= new() { StartedDay = world.Day };
            State.Cities[city.Id].Wellbeing!.FoodStock.Reconcile(city.Stocks["food"]);
            ReconcileWellbeingHomes(city);
        }
        foreach (var scout in State.Scouting?.Expeditions ?? [])
        {
            scout.ProvisionComposition ??= new(); scout.ProvisionComposition.Reconcile(scout.Food);
        }
    }

    private void ReconcileWellbeingHomes(CityState city)
    {
        if (Rules.Wellbeing is null || State.Cities[city.Id].Wellbeing is not { } state) return;
        var homes = WellbeingHomes(city); var identities = homes.Select(h => HouseholdIdentity(h.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var id in state.Households.Keys.Where(id => !identities.Contains(id)).ToArray()) state.Households.Remove(id);
        foreach (var group in homes.GroupBy(h => HouseholdIdentity(h.Id)))
        {
            if (!state.Households.TryGetValue(group.Key, out var profile)) state.Households[group.Key] = profile = NewWellbeing();
            profile.Members = group.Sum(h => h.Residents);
        }
    }

    public void ReconcileFoodComposition()
    {
        if (Rules.Wellbeing is null) return;
        foreach (var city in world.Cities.Values) State.Cities[city.Id].Wellbeing!.FoodStock.Reconcile(city.Stocks["food"]);
    }

    private string FoodCategory(string activity) => Rules.Wellbeing?.Foods.FirstOrDefault(f => f.Activities.Contains(activity))?.Id ?? "unknown";

    private void RecordFoodProduction(CityState city, string activity, double amount)
    {
        if (Rules.Wellbeing is null) return;
        var stock = State.Cities[city.Id].Wellbeing!.FoodStock;
        stock.Reconcile(Math.Max(0, city.Stocks["food"] - amount));
        stock.Add(FoodCategory(activity), amount);
    }

    // Called before the canonical scalar stock is consumed. Production alone never teaches a taste.
    public void RecordFoodConsumption(CityState city, double amount)
    {
        if (Rules.Wellbeing is null) return;
        var state = State.Cities[city.Id].Wellbeing!;
        state.FoodStock.Reconcile(city.Stocks["food"]);
        state.ConsumedToday = state.FoodStock.Take(amount);
    }

    private Dictionary<string, double> DesiredFoodShares(CityState city)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (Rules.Wellbeing is null) return result;
        var profiles = State.Cities[city.Id].Wellbeing!.Households.Values;
        var members = Math.Max(1, profiles.Sum(p => p.Members));
        foreach (var profile in profiles)
            foreach (var food in profile.Foods.Where(p => p.Value.FirstTastedDay is not null))
                Add(result, food.Key, food.Value.ExpectedShare * profile.Members / members);
        return result;
    }

    private double FoodPreference(CityState city, string homeId, HouseholdActivityRule activity)
    {
        if (Rules.Wellbeing is not { } rules || activity.Output != "food" ||
            city.Stocks["food"] < Population(city) * city.FoodPerPersonPerDay * rules.SafetyFoodDays) return 1;
        var profile = State.Cities[city.Id].Wellbeing!.Households.GetValueOrDefault(HouseholdIdentity(homeId));
        var memory = profile?.Foods.GetValueOrDefault(FoodCategory(activity.Id));
        return memory?.FirstTastedDay is not null ? 1 + rules.VarietyPriorityBonus * Math.Max(0, memory.ExpectedShare - memory.EatenShareToday) / rules.MaximumFoodExpectation : 1;
    }

    private double FoodActivityDeficit(CityState city, HouseholdActivityRule activity, double missing)
    {
        if (Rules.Wellbeing is not { } rules || activity.Output != "food" ||
            city.Stocks["food"] < Population(city) * city.FoodPerPersonPerDay * rules.SafetyFoodDays) return missing;
        var target = Target(city, "food"); var category = FoodCategory(activity.Id);
        var stock = State.Cities[city.Id].Wellbeing!.FoodStock;
        var otherPreferences = DesiredFoodShares(city).Where(p => p.Key != category)
            .Sum(p => Math.Max(0, target * p.Value - stock.Amounts.GetValueOrDefault(p.Key)));
        // Only a bounded part of a safe reserve is earmarked for desired food. No extra food is minted.
        return Math.Max(0, missing - Math.Min(target * rules.PreferenceReserveShare, otherPreferences));
    }

    private double WellbeingProjectPreference(CityState city, string householdId, CollectiveProposal proposal)
    {
        if (Rules.Wellbeing is not { } rules || State.Cities[city.Id].Wellbeing!.Households.GetValueOrDefault(householdId) is not { } profile) return 0;
        var needs = profile.Needs;
        var survival = Math.Max(needs.GetValueOrDefault("food"), needs.GetValueOrDefault("water"));
        var motive = proposal.Kind switch
        {
            "well" => needs.GetValueOrDefault("water") + needs.GetValueOrDefault("rest") * Math.Min(1, profile.WaterTravelHours / Math.Max(1, profile.WorkHours)),
            "garden" => needs.GetValueOrDefault("food") + (survival < .25 ? needs.GetValueOrDefault("rest") * .5 +
                Math.Max(0, (profile.Foods.GetValueOrDefault("crops")?.ExpectedShare ?? 0) - (profile.Foods.GetValueOrDefault("crops")?.EatenShareToday ?? 0)) : 0),
            "house" => proposal.Replaces is null || HouseholdIdentity(proposal.Replaces) == householdId ? needs.GetValueOrDefault("housing") : 0,
            _ => 0
        };
        return Math.Clamp(motive, 0, 1) * rules.ProjectPriorityBonus;
    }

    private bool WantsHousingImprovement(DwellingState b)
    {
        if (Rules.Wellbeing is not { } rules || b.Kind != "house" || b.Residents == 0 || b.Lifecycle is not { Retiring: false } age ||
            b.Status != "active" || age.AgeDays < Material(b).GraceDays) return false;
        var profile = State.Cities[b.CityId].Wellbeing!.Households.GetValueOrDefault(HouseholdIdentity(b.Id));
        // Compare with the quality attainable AFTER repair: don't replace a fixable roof for a mood score.
        return profile is not null && profile.ExpectedHousing - (1 - age.PermanentWear) > rules.HousingImprovementThreshold;
    }

    private bool WantsLaborSavingGarden(CityState city) => Rules.Wellbeing is not null &&
        State.Cities[city.Id].Wellbeing!.Needs.GetValueOrDefault("rest") > .3 &&
        (State.Cities[city.Id].Food?.WildHours ?? 0) > State.Cities[city.Id].LaborAvailableHours * .2;

    // Expectations and memories belong to people, not to the building slot they vacate.
    private void TransferWellbeing(string cityId, string fromId, string toId, int count, int alreadyThere)
    {
        if (Rules.Wellbeing is null || fromId == toId || count <= 0 || State.Cities[cityId].Wellbeing is not { } state ||
            !state.Households.TryGetValue(fromId, out var source)) return;
        if (!state.Households.TryGetValue(toId, out var target)) state.Households[toId] = target = NewWellbeing();
        var share = count / (double)Math.Max(1, count + alreadyThere);
        double Blend(double old, double incoming) => old * (1 - share) + incoming * share;
        target.ExpectedHousing = Blend(target.ExpectedHousing, source.ExpectedHousing);
        target.ExpectedRest = Blend(target.ExpectedRest, source.ExpectedRest);
        target.Satisfaction = Blend(target.Satisfaction, source.Satisfaction);
        target.ObservedDays = Math.Max(target.ObservedDays, source.ObservedDays);
        foreach (var id in source.Foods.Keys.Union(target.Foods.Keys).ToArray())
        {
            var memory = source.Foods.GetValueOrDefault(id) ?? new();
            if (!target.Foods.TryGetValue(id, out var copy)) target.Foods[id] = copy = new();
            if (memory.FirstTastedDay is { } day) copy.FirstTastedDay = Math.Min(day, copy.FirstTastedDay ?? day);
            copy.Familiarity = Blend(copy.Familiarity, memory.Familiarity);
            copy.ExpectedShare = Blend(copy.ExpectedShare, memory.ExpectedShare);
            copy.EatenShareToday = Blend(copy.EatenShareToday, memory.EatenShareToday);
        }
    }

    public void EvaluateWellbeing()
    {
        if (Rules.Wellbeing is not { } rules) return;
        ReconcileFoodComposition();
        foreach (var city in world.Cities.Values)
        {
            var life = State.Cities[city.Id]; var state = life.Wellbeing!;
            if (state.LastEvaluatedDay >= world.Day) continue;
            ReconcileWellbeingHomes(city);
            var homes = WellbeingHomes(city); var population = Math.Max(1, homes.Sum(h => h.Residents));
            var profiles = state.Households;
            var totalWork = Math.Clamp(life.LaborUsedHours + life.IndustryLaborHours, 0, life.LaborAvailableHours);
            foreach (var (id, profile) in profiles)
            {
                profile.WorkCapacityHours = life.LaborAvailableHours * profile.Members / population;
                var owned = homes.Where(h => HouseholdIdentity(h.Id) == id).Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
                profile.WorkHours = Math.Min(profile.WorkCapacityHours, life.Tasks.Where(t => owned.Contains(t.HomeId) && t.Activity is not ("repair" or "demolition" or "move")).Sum(t => t.Hours));
                profile.WaterTravelHours = life.Tasks.Where(t => owned.Contains(t.HomeId) && t.Activity == "water")
                    .Sum(t => Math.Max(0, t.Hours - t.Output / WaterCarry(city) * .08));
                profile.HousingQuality = homes.Where(h => owned.Contains(h.Id)).Sum(h => h.Residents * (h.Kind == "camp" ? 0 : Efficiency(h))) / Math.Max(1, profile.Members);
            }
            var allocated = profiles.Values.Sum(p => p.WorkHours);
            var shared = Math.Max(0, totalWork - allocated); var slack = Math.Max(1e-9, life.LaborAvailableHours - allocated);
            var unassignedWater = Math.Max(0, life.WaterTravelHours - profiles.Values.Sum(p => p.WaterTravelHours));
            var localNeed = Math.Max(0, Population(city) * city.FoodPerPersonPerDay - RemoteNeed(city.Id, "food"));
            var eaten = state.ConsumedToday.Values.Sum(); var foodCoverage = localNeed > 0 ? Math.Min(1, eaten / localNeed) : 1;
            var shares = state.ConsumedToday.ToDictionary(p => p.Key, p => p.Value / Math.Max(1e-9, eaten));
            foreach (var profile in profiles.Values)
            {
                profile.WorkHours += shared * (profile.WorkCapacityHours - profile.WorkHours) / slack;
                profile.WaterTravelHours += unassignedWater * profile.Members / population;
                profile.Observe(rules, world.Day, foodCoverage, life.WaterCoverage, shares);
            }
            state.Needs.Clear();
            foreach (var profile in profiles.Values)
                foreach (var need in profile.Needs) Add(state.Needs, need.Key, need.Value * profile.Members / population);
            state.Satisfaction = profiles.Values.Sum(p => p.Satisfaction * p.Members / population);
            var most = state.Needs.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
            state.MainConcern = most.Value > .05 ? most.Key : "none";
            state.LastEvaluatedDay = world.Day;
        }
    }
}
