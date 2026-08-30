using System.Text.Json.Nodes;
using WorldGen.Core.Simulation;
using WorldGen.Core.Settlements;
using WorldGen.Core.Topology;

namespace WorldGen.Tests;

public sealed partial class SettlementSimulationTests
{
    private static async Task<SphericalSimulation> CreateWellbeing(JsonObject? snapshot = null, bool legacy = false, bool scoutingPressure = false)
    {
        var (content, definition, economy, rules, hydro) = await Base.Value;
        rules = rules with
        {
            Wellbeing = legacy ? null : rules.Wellbeing,
            Exploration = scoutingPressure ? rules.Exploration! with { LaborPressureShare = .05 } : rules.Exploration
        };
        var topology = new CubeSphereTopology(definition.FaceSize); var generator = new SphericalTerrainGenerator(definition);
        return SphericalSimulation.Create(content, definition, economy, topology, generator, hydro,
            SphericalSettlementLayer.Build(definition, topology, generator), rules, snapshot);
    }

    private static HouseholdWellbeingState Comfortable(SettlementWellbeingRules rules) => new()
    {
        Members = 25,
        WorkCapacityHours = 100,
        WorkHours = 50,
        HousingQuality = 1,
        ExpectedHousing = rules.InitialHousingExpectation,
        ExpectedRest = rules.InitialRestExpectation
    };

    [Fact]
    public void WellbeingFoodCompositionPartitionsOneStockAndWithdrawsProportionally()
    {
        var stock = new FoodComposition(); stock.Reconcile(10);
        Assert.Equal(10, stock.Amounts["unknown"]);
        stock.Add("meat", 2); stock.Reconcile(6);
        Assert.Equal(1, stock.Amounts["meat"], 10);
        var meal = stock.Take(3);
        Assert.Equal(.5, meal["meat"], 10); Assert.Equal(2.5, meal["unknown"], 10);
        Assert.Equal(3, stock.Amounts.Values.Sum(), 10);
        Assert.Equal(3, stock.Take(100).Values.Sum(), 10); Assert.Empty(stock.Amounts);
        Assert.Throws<InvalidOperationException>(() => stock.Reconcile(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => stock.Take(-1));
    }

    [Fact]
    public void WellbeingCompositionRemainsFiniteThroughRepeatedTinyWithdrawals()
    {
        var stock = new FoodComposition(); stock.Add("meat", 1); stock.Add("fish", 2); stock.Add("crops", 3);
        var remaining = 6d;
        for (var day = 0; day < 10000; day++)
        {
            var removed = stock.Take(remaining * .01).Values.Sum(); remaining -= removed;
            stock.Reconcile(remaining);
            Assert.Equal(remaining, stock.Amounts.Values.Sum(), 10);
            Assert.All(stock.Amounts.Values, n => Assert.True(double.IsFinite(n) && n >= 0));
        }
    }

    [Fact]
    public async Task WellbeingUnknownMealsDoNotTeachTastesOrCreateVarietyDiscontent()
    {
        var (_, _, _, rules, _) = await Base.Value; var profile = Comfortable(rules.Wellbeing!);
        for (var day = 0; day < 100; day++) profile.Observe(rules.Wellbeing!, day, 1, 1, new Dictionary<string, double> { ["unknown"] = 1 });
        Assert.All(profile.Foods.Values, m => { Assert.Null(m.FirstTastedDay); Assert.Equal(0, m.ExpectedShare); });
        Assert.Equal(0, profile.Needs["variety"]); Assert.Equal(1, profile.Satisfaction, 9);
    }

    [Fact]
    public async Task WellbeingOneTasteIsSmallRegularMealsEstablishHabitAndLossAdaptsSlowly()
    {
        var (_, _, _, rules, _) = await Base.Value; var r = rules.Wellbeing!; var p = Comfortable(r);
        var meal = new Dictionary<string, double> { ["meat"] = .3, ["wild_plants"] = .7 };
        p.Observe(r, 0, 1, 1, meal); var first = p.Foods["meat"].ExpectedShare;
        Assert.InRange(first, 0.00001, .001); Assert.Equal(0, p.Foods["meat"].FirstTastedDay);
        for (var day = 1; day < 365; day++) p.Observe(r, day, 1, 1, meal);
        var familiar = p.Foods["meat"].ExpectedShare; Assert.InRange(familiar, .19, r.MaximumFoodExpectation);
        p.Observe(r, 365, 1, 1, new Dictionary<string, double> { ["wild_plants"] = 1 });
        Assert.Equal(familiar * (1 - r.ExpectationFallPerDay), p.Foods["meat"].ExpectedShare, 12);
        Assert.True(p.Needs["variety"] > .18); Assert.Null(p.Foods["fish"].FirstTastedDay);
        for (var day = 366; day < 730; day++) p.Observe(r, day, 1, 1, new Dictionary<string, double> { ["wild_plants"] = 1 });
        Assert.InRange(p.Foods["meat"].ExpectedShare, .08, .11); Assert.NotNull(p.Foods["meat"].FirstTastedDay);
    }

    [Fact]
    public async Task WellbeingHungerAndThirstCannotBeCompensatedByRestAndComfort()
    {
        var (_, _, _, rules, _) = await Base.Value; var p = Comfortable(rules.Wellbeing!); p.WorkHours = 0;
        p.Observe(rules.Wellbeing!, 0, 0, 0, new Dictionary<string, double>());
        Assert.Equal(.25, p.Satisfaction, 9); Assert.Equal(1, p.Needs["food"]); Assert.Equal(1, p.Needs["water"]);
        Assert.Equal(0, p.Needs["rest"]);
        p.WorkCapacityHours = 0; p.Observe(rules.Wellbeing!, 1, 0, 0, new Dictionary<string, double>());
        Assert.Equal(0, p.Needs["rest"]); // no workforce is not a holiday
    }

    [Fact]
    public async Task WellbeingWaterJourneysHousingAndLaborProduceDistinctNeeds()
    {
        var (_, _, _, rules, _) = await Base.Value; var p = Comfortable(rules.Wellbeing!);
        p.WaterTravelHours = 12.5; p.HousingQuality = .3; p.WorkHours = 100;
        p.Observe(rules.Wellbeing!, 0, 1, 1, new Dictionary<string, double> { ["wild_plants"] = 1 });
        Assert.Equal(.5, p.Needs["water"], 9); Assert.InRange(p.Needs["housing"], .5, .6);
        Assert.Equal(1, p.Needs["rest"]); Assert.Equal(0, p.Needs["food"]);
        for (var day = 1; day < 1000; day++) p.Observe(rules.Wellbeing!, day, 1, 1, new Dictionary<string, double>());
        Assert.InRange(p.ExpectedHousing, rules.Wellbeing!.InitialHousingExpectation, 1);
        Assert.InRange(p.ExpectedRest, rules.Wellbeing.InitialRestExpectation, rules.Wellbeing.MaximumRestExpectation);
    }

    [Fact]
    public async Task WellbeingRulesRejectImpossibleExpectationsAndUnsafeReserves()
    {
        var (_, _, _, rules, _) = await Base.Value;
        Assert.Throws<InvalidOperationException>(() => (rules.Wellbeing! with { MaximumFoodExpectation = .4 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules.Wellbeing! with { ExpectationRisePerDay = double.NaN }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { Wellbeing = rules.Wellbeing! with { SafetyFoodDays = rules.ReserveDays } }).Validate());
    }

    [Fact]
    public async Task ChangedWellbeingRulesRejectAnOldWorld()
    {
        var old = await CreateWellbeing(legacy: true); old.Advance(5); old.World.Day = 5000;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateWellbeing(WorldSnapshot.Create(old.World)));
    }

    [Fact]
    public async Task WellbeingActualMealsAndWorkAreAccountedAndSnapshotContinuesExactly()
    {
        var sim = await CreateWellbeing(); sim.Advance(45);
        foreach (var city in sim.World.Cities.Values)
        {
            var life = sim.Development!.State.Cities[city.Id]; var state = life.Wellbeing!;
            Assert.Equal(city.Stocks["food"], state.FoodStock.Amounts.Values.Sum(), 8);
            Assert.Equal(life.LaborUsedHours + life.IndustryLaborHours, state.Households.Values.Sum(p => p.WorkHours), 7);
            Assert.InRange(state.ConsumedToday.Values.Sum(), 0, sim.World.Spatial.Nodes[city.SpatialNodeId].Aggregate.Population * city.FoodPerPersonPerDay + .01);
            Assert.Contains(state.Households.Values, p => p.Foods.Values.Any(m => m.FirstTastedDay.HasValue));
        }
        var restored = await CreateWellbeing(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        sim.Advance(20); restored.Advance(20);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task WellbeingNoWorkersDoesNotProduceFoodOrInventTasteFromStoredFood()
    {
        var sim = await CreateWellbeing();
        foreach (var city in sim.World.Cities.Values.ToArray()) sim.World.Cities[city.Id] = city with { WorkerShare = 0 };
        sim.Advance(3);
        foreach (var life in sim.Development!.State.Cities.Values)
        {
            Assert.Equal(0, life.Production.GetValueOrDefault("food"));
            Assert.All(life.Wellbeing!.Households.Values, p =>
            { Assert.Equal(0, p.WorkHours); Assert.All(p.Foods.Values, m => Assert.Null(m.FirstTastedDay)); });
        }
    }

    [Fact]
    public async Task WellbeingMemoriesFollowPeopleDuringPaidGradualMove()
    {
        var sim = await CreateWellbeing(); sim.Advance(1); var state = sim.Development!.State;
        var source = state.Buildings.First(b => b.Kind == "house" && b.Residents == 25);
        var life = state.Cities[source.CityId]; var city = sim.World.Cities[source.CityId];
        var cell = sim.Addresses.Values.First(c => sim.World.Spatial.Territories[SphericalSimulation.ZoneId(c)].AssignedCityId == city.Id &&
            sim.World.Spatial.Territories[SphericalSimulation.ZoneId(c)].Terrain == "land" && !state.Buildings.Any(b => b.Cell == c));
        var target = new DwellingState
        {
            Id = "wellbeing-move",
            HouseholdId = "wellbeing-move",
            CityId = city.Id,
            Kind = "house",
            Cell = cell,
            Status = "active",
            Replaces = source.Id,
            MoveFinished = false,
            LaborDone = 80
        };
        state.Buildings.Add(target);
        life.Wellbeing!.Households[source.HouseholdId!].Foods["meat"] = new() { FirstTastedDay = 0, Familiarity = .9, ExpectedShare = .15 };
        sim.Development.RehousePopulation();
        sim = await CreateWellbeing(WorldSnapshot.Create(sim.World)); sim.Advance(1); state = sim.Development!.State;
        target = state.Buildings.Single(b => b.Id == target.Id); source = state.Buildings.Single(b => b.Id == source.Id);
        var memories = state.Cities[city.Id].Wellbeing!.Households;
        Assert.InRange(target.Residents, 1, 3); Assert.Equal(25, source.Residents + target.Residents);
        Assert.Equal(0, memories[target.HouseholdId!].Foods["meat"].FirstTastedDay);
        Assert.Equal(memories[source.HouseholdId!].Foods["meat"].ExpectedShare, memories[target.HouseholdId!].Foods["meat"].ExpectedShare, 10);
        Assert.True(memories[target.HouseholdId!].Foods["meat"].ExpectedShare > .14);
        var restored = await CreateWellbeing(WorldSnapshot.Create(sim.World));
        sim.Advance(4); restored.Advance(4); Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task WellbeingUnsafeHousingDoesNotEraseDisplacedPeoplesMemory()
    {
        var sim = await CreateWellbeing(); var state = sim.Development!.State;
        var city = sim.World.Cities["river_hearth"]; var life = state.Cities[city.Id];
        foreach (var home in state.Buildings.Where(b => b.CityId == city.Id && b.Residents > 0))
        {
            home.Lifecycle!.PermanentWear = 1;
            life.Wellbeing!.Households[home.HouseholdId!].Foods["meat"] = new() { FirstTastedDay = 0, Familiarity = 1, ExpectedShare = .15 };
        }
        sim.Advance(1);
        var camp = life.Wellbeing!.Households[$"camp:{city.Id}"];
        Assert.True(camp.Members > 0); Assert.Equal(0, camp.Foods["meat"].FirstTastedDay);
        Assert.True(camp.Foods["meat"].ExpectedShare > .14);
        var restored = await CreateWellbeing(WorldSnapshot.Create(sim.World));
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
    }

    [Fact]
    public async Task WellbeingHousingHabitCreatesAnImprovementProposalButRepairableDamageDoesNot()
    {
        var first = await CreateWellbeing(); var second = await CreateWellbeing(); var repairable = await CreateWellbeing();
        foreach (var sim in new[] { first, second, repairable })
        {
            var home = sim.Development!.State.Buildings.First(b => b.Kind == "house" && b.Residents == 25);
            home.Lifecycle!.AgeDays = 800; home.Lifecycle.PermanentWear = .25;
            var profile = sim.Development.State.Cities[home.CityId].Wellbeing!.Households[home.HouseholdId!];
            profile.ExpectedHousing = sim == first ? .7 : .99;
            if (sim == repairable) { home.Lifecycle.PermanentWear = 0; home.Lifecycle.RepairableWear = .25; }
            sim.Advance(1);
        }
        Assert.DoesNotContain(first.Development!.State.Cities.Values.SelectMany(l => l.Council!.Proposals), p => p.Reason.Contains("привычного"));
        var proposal = Assert.Single(second.Development!.State.Cities.Values.SelectMany(l => l.Council!.Proposals), p => p.Reason.Contains("привычного"));
        Assert.Equal("house", proposal.Kind); Assert.True(proposal.Support > 0); Assert.NotEqual("executing", proposal.Phase);
        Assert.DoesNotContain(repairable.Development!.State.Cities.Values.SelectMany(l => l.Council!.Proposals), p => p.Reason.Contains("привычного"));
    }

    [Fact]
    public async Task WellbeingKnownFoodPreferenceChangesActualHarvestWithoutMintingExtraFood()
    {
        var first = await CreateWellbeing(); first.Advance(120);
        var second = await CreateWellbeing(WorldSnapshot.Create(first.World));
        foreach (var life in second.Development!.State.Cities.Values)
            foreach (var profile in life.Wellbeing!.Households.Values)
            {
                foreach (var food in profile.Foods.Values) food.ExpectedShare = 0;
                profile.Foods["fish"] = new() { FirstTastedDay = 0, Familiarity = 1, ExpectedShare = .2, EatenShareToday = 0 };
            }
        var firstFish = 0d; var secondFish = 0d;
        for (var day = 0; day < 10; day++)
        {
            first.Advance(1); second.Advance(1);
            firstFish += first.Development!.State.Cities.Values.SelectMany(l => l.Tasks).Where(t => t.Activity == "fish").Sum(t => t.Output);
            secondFish += second.Development!.State.Cities.Values.SelectMany(l => l.Tasks).Where(t => t.Activity == "fish").Sum(t => t.Output);
            foreach (var city in second.World.Cities.Values)
            {
                var life = second.Development.State.Cities[city.Id];
                Assert.Equal(city.Stocks["food"], life.Wellbeing!.FoodStock.Amounts.Values.Sum(), 8);
                Assert.InRange(life.LaborUsedHours, 0, life.LaborAvailableHours + 1e-6);
            }
        }
        output.WriteLine($"actual fish harvest: normal={firstFish:F4}t, accustomed={secondFish:F4}t");
        Assert.True(secondFish > firstFish + .001);
    }

    [Fact]
    public async Task WellbeingScoutProvisionsKeepTheirCompositionThroughReturnAndResume()
    {
        var sim = await CreateWellbeing(scoutingPressure: true);
        foreach (var city in sim.World.Cities.Values)
        {
            var life = sim.Development!.State.Cities[city.Id];
            life.Supply!.PressureStreak = sim.Development.Rules.Exploration!.PressureDays;
            life.Supply.LastDepartureDay = -10000;
            city.Stocks["water"] = 5; city.Stocks["food"] = 3;
            life.Wellbeing!.FoodStock.Amounts = new() { ["fish"] = 1, ["wild_plants"] = 2 };
        }
        var launchWait = 0;
        while (!(sim.Development!.State.Scouting?.Expeditions.Any(e => e.Phase is "outbound" or "returning") ?? false) && launchWait++ < 20)
            sim.Advance(1);
        var state = sim.Development!.State;
        Assert.Contains(state.Scouting!.Expeditions, e => e.Phase is "outbound" or "returning");
        Assert.All(state.Scouting.Expeditions, e =>
        { Assert.Equal(e.Food, e.ProvisionComposition!.Amounts.Values.Sum(), 10); Assert.True(e.ProvisionComposition.Amounts.GetValueOrDefault("fish") > 0); });
        var restored = await CreateWellbeing(WorldSnapshot.Create(sim.World), scoutingPressure: true);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        var returnWait = 0;
        while (state.Scouting.Expeditions.Any(e => e.Phase is "outbound" or "returning") && returnWait++ < 40)
        {
            sim.Advance(1); restored.Advance(1);
        }
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(restored.World));
        Assert.All(state.Scouting.Expeditions, e =>
        { Assert.Equal("returned", e.Phase); Assert.Equal(0, e.Food); Assert.Equal(0, e.ProvisionComposition!.Amounts.Values.Sum()); });
        var afterReturn = await CreateWellbeing(WorldSnapshot.Create(sim.World), scoutingPressure: true);
        Assert.Equal(WorldSnapshot.Hash(sim.World), WorldSnapshot.Hash(afterReturn.World));
    }

    [Fact]
    public async Task WellbeingFourYearsRemainBoundedAndBudgetedWithHabitFormation()
    {
        var sim = await CreateWellbeing(); var tasted = false; var variety = 0d;
        for (var day = 0; day < 1460; day++)
        {
            sim.Advance(1);
            foreach (var city in sim.World.Cities.Values)
            {
                var life = sim.Development!.State.Cities[city.Id]; var state = life.Wellbeing!;
                Assert.Equal(city.Stocks["food"], state.FoodStock.Amounts.Values.Sum(), 7);
                Assert.InRange(state.Satisfaction, 0, 1);
                Assert.InRange(life.LaborUsedHours + life.IndustryLaborHours, 0, life.LaborAvailableHours + 1e-6);
                Assert.InRange(state.Households.Count, 1, sim.Development.State.Buildings.Count(b => b.CityId == city.Id && b.Residents > 0) + 1);
                foreach (var p in state.Households.Values)
                {
                    Assert.InRange(p.WorkHours, 0, p.WorkCapacityHours + 1e-6);
                    Assert.All(p.Needs.Values, n => Assert.InRange(n, 0, 1));
                    Assert.InRange(p.Foods.Count, 0, 4);
                    Assert.All(p.Foods.Values, m => Assert.InRange(m.ExpectedShare, 0, sim.Development.Rules.Wellbeing!.MaximumFoodExpectation));
                    tasted |= p.Foods.Values.Count(m => m.FirstTastedDay is not null) >= 2;
                }
                variety = Math.Max(variety, state.Needs.GetValueOrDefault("variety"));
            }
            if ((day + 1) % 365 == 0) output.WriteLine($"wellbeing year={(day + 1) / 365}, pop={sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population}, " +
                string.Join("; ", sim.Development!.State.Cities.Select(p => $"{p.Key}: satisfaction={p.Value.Wellbeing!.Satisfaction:F3}, concern={p.Value.Wellbeing.MainConcern}, food={p.Value.Wellbeing.ConsumedToday.Values.Sum():F3}")));
        }
        Assert.True(tasted); Assert.True(variety > .001);
        Assert.All(sim.World.Cities.Values, city => Assert.False(city.Shortage.Active));
        Assert.True(sim.World.Spatial.Nodes[sim.World.Spatial.RegionNodeId].Aggregate.Population >= 470);
    }
}
