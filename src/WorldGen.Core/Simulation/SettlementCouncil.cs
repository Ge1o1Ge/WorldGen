using System.Text.Json.Nodes;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private sealed record BuildingIdea(string Key, string Kind, string Domain, string Reason, double Complexity,
        IReadOnlyList<CellAddress> Sites, string? Replaces = null);

    // Legacy snapshots used whole-household replacement. New split households
    // have explicit identities; their membership and accumulated support move together.
    public string HouseholdIdentity(string buildingId)
    {
        var id = buildingId; var seen = new HashSet<string>();
        while (seen.Add(id))
        {
            var home = State.Buildings.FirstOrDefault(b => b.Id == id);
            if (home?.HouseholdId is { } identity) return identity;
            if (home?.Replaces is not { } previous) break;
            id = previous;
        }
        return id;
    }

    private DwellingState? CouncilConstruction(CityState city, DwellingState[] homes, DwellingState? construction, DailyTelemetry telemetry)
    {
        var rules = Rules.Decisions!; var life = State.Cities[city.Id];
        var council = life.Council ??= new();
        var groups = homes.Where(h => h.Residents > 0).Select(h => (Id: HouseholdIdentity(h.Id), h.Residents, h.Cell)).ToList();
        if (life.Unhoused > 0) groups.Add(($"camp:{city.Id}", life.Unhoused, addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!]));
        foreach (var profile in council.Profiles.Values) profile.Members = 0;
        foreach (var group in groups)
        {
            if (!council.Profiles.TryGetValue(group.Id, out var profile)) council.Profiles[group.Id] = profile = new();
            profile.Members = group.Residents;
        }
        foreach (var task in life.Tasks)
        {
            var id = HouseholdIdentity(task.HomeId);
            if (!council.Profiles.TryGetValue(id, out var profile)) continue;
            var domain = task.Activity == "water" ? "water" : task.Activity is "wood" or "fuel" ? "construction" :
                task.Activity is "gather" or "hunt" or "fish" or "cultivate" ? "food" : null;
            if (domain is not null) profile.PracticeHours[domain] = Math.Min(profile.Members * rules.LearningHoursPerPerson,
                profile.PracticeHours.GetValueOrDefault(domain) + task.Hours);
        }
        var ideas = BuildingIdeas(city, homes);
        foreach (var p in council.Proposals.Where(CollectiveDecisions.Pending))
        {
            p.Available = ideas.Any(i => i.Key == p.Key);
            foreach (var site in p.Sites) site.Available = ValidCouncilSite(city, p.Kind, site.Cell) &&
                (p.Kind == "scouting" || groups.Any(h => Routes(h.Cell).Cost.ContainsKey(site.Cell)));
            p.Available &= p.Sites.Any(s => s.Available);
        }
        foreach (var idea in ideas)
        {
            if (council.Proposals.Any(p => (p.Key == idea.Key || idea.Kind == "scouting" && p.Kind == "scouting") &&
                p.Phase is "idea" or "site" or "approved" or "executing" or "observing")) continue;
            var budget = groups.Sum(g => g.Residents) * rules.DailyPointsPerPerson;
            var p = new CollectiveProposal
            {
                Id = $"decision:{city.Id}:{council.NextId++}",
                Key = idea.Key,
                Scope = city.Id,
                Kind = idea.Kind,
                Domain = idea.Domain,
                Reason = idea.Reason,
                Replaces = idea.Replaces,
                CreatedDay = world.Day,
                RequiredSupport = Math.Max(10, budget * rules.ApprovalDays * idea.Complexity),
                RequiredSiteSupport = Math.Max(2, budget * rules.SiteDays),
                Sites = idea.Sites.Select(c => new DecisionSite { Id = SphericalSimulation.ZoneId(c), Cell = c }).ToList()
            };
            p.CauseEventId = Journal.Record(world, "decision_proposed", p.Id, details: new JsonObject
            { ["cityId"] = city.Id, ["kind"] = p.Kind, ["reason"] = p.Reason, ["threshold"] = p.RequiredSupport }).Id;
            council.Proposals.Add(p);
        }
        var pending = council.Proposals.Where(p => p.Available && p.Phase is "idea" or "site").ToArray();
        var electors = groups.Select(g => new DecisionElector(g.Id, city.Id, g.Residents,
            pending.Select(p => p.Id).ToHashSet(StringComparer.Ordinal),
            pending.SelectMany(p => p.Sites).Where(s => s.Available &&
                (pending.Any(p => p.Kind == "scouting" && p.Sites.Contains(s)) || Routes(g.Cell).Cost.ContainsKey(s.Cell))).Select(s => s.Id).ToHashSet(StringComparer.Ordinal))).ToArray();
        var ballots = new List<DecisionBallot>();
        foreach (var group in groups)
        {
            double Preference(CollectiveProposal p) => WellbeingProjectPreference(city, group.Id, p) + (p.Key.StartsWith("replace-worn:", StringComparison.Ordinal) ? 3.5 :
                p.Kind == "scouting" ? 2.5 + (life.Supply?.PressureStreak ?? 0) / Math.Max(1d, Rules.Exploration?.PressureDays ?? 1) * 2 :
                p.Kind == "garden" ? 1.5 + (life.Food?.LaborHours ?? 0) / Math.Max(1, life.LaborAvailableHours) * 3 :
                p.Kind == "well" ? Math.Min(3, WaterDistance(group.Cell, city.Id) / 3) :
                p.Kind == "granary" ? 1.5 + Math.Min(4, FoodStorageNeedVolume(city)) :
                p.Kind == "warehouse" ? 1.2 + Math.Min(4, OutdoorStorageVolume(city, false) * .5) :
                BuildingRule(p.Kind).Technology is not null ? 2 + (life.Processes.Values.Any(process => process.Constraint?.StartsWith("building:any:", StringComparison.Ordinal) == true) ? 2 : 0) :
                p.Replaces is not null ? (HouseholdIdentity(p.Replaces) == group.Id ? 3 : .35) : life.Unhoused > 0 ? 4 : 1.2);
            var preferred = pending.OrderByDescending(Preference).ThenBy(p => p.CreatedDay).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            if (preferred is null || Preference(preferred) <= 0) continue;
            string? siteId = null;
            if (preferred.Phase == "site")
            {
                // Shared access matters as well as a household's own journey.
                // Only known, reachable sites enter this comparison.
                var site = preferred.Sites.Where(s => s.Available && (preferred.Kind == "scouting" || Routes(group.Cell).Cost.ContainsKey(s.Cell)))
                    .OrderBy(s => preferred.Kind == "garden" ?
                        (1 + groups.Sum(g => Routes(g.Cell).Cost.GetValueOrDefault(s.Cell, 1000) * g.Residents) / Math.Max(1, Population(city)) * .08) / Math.Max(.01, terrain[s.Cell].NaturalState.SoilQuality) :
                        preferred.Kind == "scouting" ? 0 :
                        Routes(group.Cell).Cost[s.Cell] * .5 + groups.Sum(g => Routes(g.Cell).Cost.GetValueOrDefault(s.Cell, 1000) * g.Residents) / Math.Max(1, Population(city)) * .5)
                    .ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();
                if (site is null) continue;
                siteId = site.Id;
            }
            ballots.Add(new(group.Id, preferred.Id, siteId, 1));
        }
        var phases = council.Proposals.ToDictionary(p => p.Id, p => p.Phase, StringComparer.Ordinal);
        var delegations = new List<DecisionDelegation>();
        if (Rules.Primitive is { } era && Knows(city, "chieftains") && groups.Count > 1 && ballots.Count > 0)
        {
            var representative = groups.Where(g => ballots.Any(b => b.VoterId == g.Id))
                .OrderByDescending(g => council.Profiles[g.Id].PracticeHours.Values.Sum() / Math.Max(1, g.Residents))
                .ThenBy(g => g.Id, StringComparer.Ordinal).First().Id;
            life.Primitive!.Representative = representative;
            delegations.AddRange(groups.Where(g => g.Id != representative).Select(g => new DecisionDelegation(g.Id, representative, era.ChiefDelegationShare)));
            ballots = ballots.Select(b => b.VoterId == representative ? b : b with { Share = b.Share * (1 - era.ChiefDelegationShare) }).ToList();
        }
        CollectiveDecisions.Advance(council, rules, world.Day, electors, ballots, delegations);
        foreach (var p in council.Proposals.Where(p => phases.GetValueOrDefault(p.Id) != p.Phase))
            Journal.Record(world, "decision_stage_changed", p.Id, [p.CauseEventId], new JsonObject
            { ["cityId"] = city.Id, ["phase"] = p.Phase, ["reason"] = p.Reason });
        if (construction is not null) return construction;
        foreach (var p in council.Proposals.Where(p => p.Phase == "approved").OrderBy(p => p.CreatedDay).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (p.Kind == "scouting") { life.Decision = "Совет одобрил сбор разведывательной группы; готовятся припасы"; continue; }
            var site = p.Sites.Single(s => s.Id == p.SelectedSite);
            if (!p.Available || !ValidCouncilSite(city, p.Kind, site.Cell)) continue;
            var project = StartProject(city, p.Kind, site.Cell, telemetry, p.Reason, p.Replaces, p.CauseEventId);
            if (project is null) { life.Decision = $"Совет одобрил: {p.Reason}; ожидаются материалы"; continue; }
            CollectiveDecisions.MarkStarted(p, project.Id, world.Day);
            return project;
        }
        var discussed = council.Proposals.Where(CollectiveDecisions.Pending).OrderBy(p => p.CreatedDay).FirstOrDefault();
        if (discussed is not null && discussed.Phase != "approved")
            life.Decision = discussed.Phase == "idea" ? $"Обсуждение: {discussed.Reason}" : $"Выбор места: {discussed.Reason}";
        else if (discussed is null) life.Decision = "Новых строительных решений не требуется";
        return null;
    }

    private bool ValidCouncilSite(CityState city, string kind, CellAddress cell)
    {
        if (kind == "scouting") return SurveyTerrain(cell) is { } scout && (!scout.Water || Knows(city, "rafts"));
        return terrain.TryGetValue(cell, out var t) &&
        t.AssignedCityId == city.Id && t.Terrain != "water" && Free(cell) &&
        (kind != "garden" || Rules.Subsistence is not null && State.Cities[city.Id].Discoveries.Contains("gardening") &&
            layer.Construction.GetOccupiedCapacity(cell) == 0 && t.Fertility >= .35 && CanCultivate(cell) && CanStartCropPlot(city,cell)) &&
        (kind != "well" || State.Cities[city.Id].Discoveries.Contains("well") && t.Moisture >= .42 && t.ElevationMeters < 450 &&
            (Rules.Lifecycle is null || t.Water.DistanceToRiver != 0 && !State.Buildings.Any(b => b.Cell == cell && b.Kind == "well" && Standing(b)))) &&
        (BuildingRule(kind).Site != "river" || t.Water.DistanceToRiver == 0) &&
        (BuildingRule(kind).Site != "wind" || t.ForestCover <= .3 && t.ElevationMeters >= 20);
    }

    private List<BuildingIdea> BuildingIdeas(CityState city, DwellingState[] homes)
    {
        var ideas = new List<BuildingIdea>();
        var life = State.Cities[city.Id]; var anchor = homes.FirstOrDefault()?.Cell ?? addresses[world.Spatial.Nodes[city.SpatialNodeId].AnchorTerritoryId!];
        CellAddress[] Sites(string kind) => Routes(anchor).Cost.Keys.Where(c => ValidCouncilSite(city, kind, c))
            .OrderBy(c => Routes(anchor).Cost[c] + terrain[c].ForestCover * .1).ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Take(3).ToArray();
        var ordinary = Sites("house");
        if (Rules.Exploration is { } exploration && life.Supply is { } supply && IsForager &&
            supply.PressureStreak >= exploration.PressureDays && world.Day - supply.LastDepartureDay >= exploration.CooldownDays &&
            !(State.Scouting?.Expeditions.Any(e => e.CityId == city.Id && ActiveScout(e)) ?? false))
        {
            var direction = ScoutDirection(city, life.Council?.NextId ?? State.Scouting?.NextId ?? 1, Knows(city, "rafts"));
            ideas.Add(new($"scouting:{supply.PressureEventId ?? "supply"}", "scouting", "organization",
                $"Собрать разведывательную группу: {supply.Reason}", exploration.DecisionComplexity, [direction]));
        }
        if (Rules.Lifecycle is not null)
            foreach (var old in State.Buildings.Where(b => b.CityId == city.Id && (NeedsReplacement(b) || WantsHousingImprovement(b)) && (b.Kind != "house" || b.Residents > 0))
                .Where(b => !State.Buildings.Any(next => next.Replaces == b.Id && Standing(next))).OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                var sites = Sites(old.Kind);
                if (sites.Length > 0) ideas.Add(new($"replace-worn:{old.Id}", old.Kind, old.Kind == "well" ? "water" : "construction",
                    old.Kind == "house" ? NeedsReplacement(old) ? "Заменить ветхий дом до потери жилья" : "Качество жилья ниже привычного; ремонт уже не восстановит удобство" :
                    old.Kind == "well" ? "Заменить изношенный колодец" : "Заменить изношенную производственную установку",
                    Rules.Decisions!.Complexity[old.Kind], sites, old.Id));
            }
        if (Rules.Subsistence is { } subsistence && life.Discoveries.Contains("gardening") &&
            ((life.Food?.LaborHours ?? 0) > life.LaborAvailableHours * subsistence.FoodLaborPressure || life.Supply?.RenewalCoverage < .85 || WantsLaborSavingGarden(city)))
        {
            var gardens = State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "garden" && Standing(b)).ToArray();
            var existingYield = gardens.Sum(b => GardenYield(b.Cell));
            if (existingYield < Population(city) * city.FoodPerPersonPerDay * subsistence.GardenMaximumFoodShare && (BiologyRules is null || gardens.Length<Math.Max(2,Population(city)*.65)))
            {
                var sites = Routes(anchor).Cost.Keys.Where(c => ValidCouncilSite(city, "garden", c))
                    .OrderByDescending(c => terrain[c].NaturalState.SoilQuality / (1 + Routes(anchor).Cost[c] * .08 + terrain[c].NaturalState.ForestBiomass))
                    .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Take(3).ToArray();
                if (sites.Length > 0) ideas.Add(new($"prepare-garden:{gardens.Length}", "garden", "food", "Освоить огород: дикая пища обходится всё дороже", 2, sites));
            }
        }
        if (Rules.Subsistence is not null && life.Discoveries.Contains("gardening") && PendingTechnicalCropTrials(city) is { Length: > 0 } trials)
        {
            var crop = trials[0];
            // Land preparation may start before the sowing window; the crop
            // simulation itself will wait for viable temperature and season.
            var sites = Routes(anchor).Cost.Keys.Where(c => ValidCouncilSite(city, "garden", c) && CanSupportCropCycle(crop, c))
                .OrderByDescending(c => CropSuitability(crop, c) / (1 + Routes(anchor).Cost[c] * .08 + terrain[c].NaturalState.ForestBiomass))
                .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Take(3).ToArray();
            if (sites.Length > 0) ideas.Add(new($"technical-crop:{crop.Id}", "garden", "craft",
                $"Опытный участок: проверить пользу культуры «{crop.Name}» вне пищевого хозяйства", 2, sites));
        }
        if (ordinary.Length > 0 && life.HousingCapacity - Population(city) < Math.Ceiling(Population(city) * .05))
            ideas.Add(new("housing-reserve", "house", "construction", "Нехватка жилья или резерв для роста", Rules.Decisions!.Complexity["house"], ordinary));
        if (ordinary.Length > 0 && life.Discoveries.Contains("storage_buildings") && OutdoorStorageVolume(city, false) > .25)
        {
            var count = State.Buildings.Count(b => b.CityId == city.Id && b.Kind == "warehouse" && Standing(b));
            ideas.Add(new($"warehouse:{count}", "warehouse", "construction", "Запасы под открытым небом портятся; требуется общий склад",
                Rules.Decisions!.Complexity["warehouse"], Sites("warehouse")));
        }
        if (ordinary.Length > 0 && life.Discoveries.Contains("granary") && FoodStorageNeedVolume(city) > .08)
        {
            var count = State.Buildings.Count(b => b.CityId == city.Id && b.Kind == "granary" && Standing(b));
            ideas.Add(new($"granary:{count}", "granary", "food", "Урожаю и семенам не хватает сухого защищённого хранения",
                Rules.Decisions!.Complexity["granary"], Sites("granary")));
        }
        if (life.Discoveries.Contains("well") && life.WaterTravelHours > Population(city) * .035 &&
            (Rules.Lifecycle is null ? !State.Buildings.Any(b => b.CityId == city.Id && b.Kind == "well" && Standing(b)) :
                State.Buildings.Where(b => b.CityId == city.Id && b.Kind == "well" && Standing(b)).Sum(b => b.Status == "building" ? Rules.Lifecycle.WellRechargePerDay : b.Well?.RechargeRate ?? 0) < Population(city) * .005 * 1.05))
        {
            var sites = Sites("well");
            if (sites.Length > 0) ideas.Add(new("well", "well", "water", "Сократить труд на доставку воды", Rules.Decisions!.Complexity["well"], sites));
        }
        if (Rules.Primitive is { } primitive)
            foreach (var rule in Rules.Buildings.Where(rule => rule.Technology is not null).OrderBy(rule => rule.Id, StringComparer.Ordinal))
            {
                if (!life.Discoveries.Contains(rule.Technology!) || State.Buildings.Any(building => building.CityId == city.Id && building.Kind == rule.Id && Standing(building))) continue;
                var blockedProcesses = primitive.Processes.Where(process => process.BuildingRequirements.Contains(rule.Id, StringComparer.Ordinal) &&
                    life.Discoveries.Contains(process.Technology) && !process.BuildingRequirements.Any(kind => State.Buildings.Any(building => building.CityId == city.Id && building.Kind == kind && Standing(building))) &&
                    city.Stocks.GetValueOrDefault(process.TargetResource) + 1e-9 < Population(city) * process.TargetOutputPerPerson).ToArray();
                if (blockedProcesses.Length == 0) continue;
                var sites = Sites(rule.Id); if (sites.Length == 0) continue;
                ideas.Add(new($"production:{rule.Id}", rule.Id, "construction",
                    $"Механизированный процесс «{blockedProcesses[0].Name}» упирается в отсутствие подходящей установки",
                    Rules.Decisions!.Complexity[rule.Id], sites));
            }
        if (world.Day >= 30 && world.Day - life.LastRelocationDay >= Rules.RelocationCooldownDays && !State.Buildings.Any(b => b.CityId == city.Id && Moving(b)))
        {
            var worst = homes.Where(h => h.Residents > 0).OrderByDescending(h => WaterDistance(h.Cell, city.Id)).FirstOrDefault();
            if (worst is not null && WaterDistance(worst.Cell, city.Id) > 4)
            {
                var sites = Routes(worst.Cell).Cost.Keys.Where(c => ValidCouncilSite(city, "house", c) && WaterDistance(c, city.Id, quick: true) == 0)
                    .OrderBy(c => Routes(worst.Cell).Cost[c]).ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).Take(3).ToArray();
                if (sites.Length > 0) ideas.Add(new($"relocate:{worst.Id}", "house", "construction", "Переезд ближе к воде", Rules.Decisions!.Complexity["relocation"], sites, worst.Id));
            }
        }
        return ideas;
    }

    private void ObserveCouncilResults(CityState city, CollectiveDecisionState council, DecisionRules rules)
    {
        var life = State.Cities[city.Id];
        foreach (var p in council.Proposals.Where(p => p.Phase is "executing" or "observing"))
        {
            if (p.Kind == "scouting") { ObserveScoutDecision(city, council, p, rules); continue; }
            var building = State.Buildings.FirstOrDefault(b => b.Id == p.BuildingId);
            if (p.Phase == "executing" && building?.Status == "active")
            { p.Phase = "observing"; p.FinishedDay = world.Day; }
            if ((p.Phase == "executing" || BiologyRules is not null&&p.Phase=="observing") && building?.Status is "abandoned" or "demolishing" or "demolished")
            { p.Phase = "uncertain"; p.OutcomeNote = "Исполнение прервано; вина сторон не установлена"; }
            if (p.Phase != "observing") continue;
            // Growing time is not evidence that a completed garden is useless.
            if (building?.Kind == "garden" && world.Day < building.ReadyDay) continue;
            var cropPlot=building?.Kind=="garden"?life.Biology?.Plots.GetValueOrDefault(building.Id):null;
            if(BiologyRules is not null&&building?.Kind=="garden"&&cropPlot?.TotalHarvested is not >0&&cropPlot?.FailedSeasons is not >=2)
            {
                var maturity=BiologyRules.Crops.FirstOrDefault(c=>c.Id==cropPlot?.CropId)?.MatureYears??0;
                if(world.Day-p.FinishedDay<(maturity+2)*world.Calendar.DaysPerYear)continue;
                p.Phase="uncertain";p.OutcomeNote="Посадка не дала проверяемого результата за ожидаемый срок; повторное решение не блокируется";continue;
            }
            // Do not attribute observations during an active external crisis to
            // voters. Uncertain outcomes eventually close without a reputation penalty.
            if (city.ActiveEffects.Count == 0 && !city.Shortage.Active && life.WaterCoverage >= .95 && building is not null)
            {
                var benefit = -1d;
                if (building is { Status: "active", Kind: "garden" })
                    benefit = cropPlot is not null ? (cropPlot.TotalHarvested>0?.6:-1) : Math.Clamp(gardenTaken.GetValueOrDefault(building.Cell) / Math.Max(1e-9, GardenYield(building.Cell)) * 2 - 1, -1, 1);
                if (building.Status == "active" && building.Kind == "house")
                    benefit = building.Residents > 0 ? Math.Min(1, building.Residents / 12.5) :
                        life.HousingCapacity - Rules.ResidentsPerHouse < Population(city) * 1.05 ? .5 : -1;
                else if (building.Status == "active" && (building.Kind is "warehouse" or "granary") && life.Storage is { } storage)
                    benefit = Math.Clamp(storage.UsedByBuildingKind.GetValueOrDefault(building.Kind) /
                        Math.Max(.001, storage.CapacityByBuildingKind.GetValueOrDefault(building.Kind)) * 2 - 1, -1, 1);
                else if (building.Status == "active" && building.Kind == "well" && terrain[building.Cell].Water.DistanceToRiver != 0)
                {
                    var delivered = life.Tasks.Where(t => t.Activity == "water" && t.Destination == building.Cell).Sum(t => t.Output);
                    benefit = Math.Clamp((delivered / Math.Max(.001, life.WaterCollected) - .1) / .3, -1, 1);
                }
                else if (building is { Status: "active" } && BuildingRule(building.Kind).Technology is not null)
                {
                    var batches = life.Tasks.Where(task => task.HomeId == building.Id && task.Activity.StartsWith("process:", StringComparison.Ordinal)).Sum(task => task.Output);
                    benefit = batches > 0 ? Math.Clamp(.35 + batches, .35, 1) : -.5;
                }
                p.ObservedDays++; p.ObservedBenefit += benefit;
            }
            if (CollectiveDecisions.Assess(council, p, rules, world.Day, p.ObservedBenefit / Math.Max(1, p.ObservedDays), 1,
                "Использование постройки за период без наблюдаемого кризиса; полная экономическая окупаемость пока не оценена"))
                Journal.Record(world, "decision_assessed", p.Id, [p.CauseEventId], new JsonObject
                { ["cityId"] = city.Id, ["outcome"] = p.Outcome, ["observedDays"] = p.ObservedDays, ["reason"] = p.Reason });
            else if (world.Day - p.FinishedDay >= rules.MaximumEvaluationDays)
            { p.Phase = "uncertain"; p.OutcomeNote = "Недостаточно однозначных наблюдений для оценки"; }
        }
    }

    private void ObserveScoutDecision(CityState city, CollectiveDecisionState council, CollectiveProposal proposal, DecisionRules rules)
    {
        var expedition = State.Scouting?.Expeditions.FirstOrDefault(e => e.Id == proposal.BuildingId);
        if (proposal.Phase == "executing")
        {
            if (expedition?.Phase == "returned") { proposal.Phase = "observing"; proposal.FinishedDay = expedition.ReturnDay ?? world.Day; }
            else if (expedition?.Phase == "lost" && world.Day - expedition.DepartureDay > expedition.ProvisionDays + expedition.ExtensionDays + 3)
            {
                proposal.Phase = "observing"; proposal.FinishedDay = world.Day;
                proposal.OutcomeNote = "Группа не вернулась после исчерпания расчётного срока; точная причина неизвестна";
            }
            else return;
        }
        if (proposal.Phase != "observing" || expedition is null) return;
        var report = State.Cities[city.Id].Supply?.Reports.LastOrDefault(r => r.ExpeditionId == expedition.Id);
        var discoveries = (report?.Plants?.Count ?? 0) + (report?.Animals?.Count ?? 0);
        var benefit = expedition.Phase == "lost" ? -1 : Math.Clamp((report?.SurveyedCells ?? 0) / 24d + discoveries * .08 - expedition.Casualties * .35, -1, 1);
        proposal.ObservedDays++; proposal.ObservedBenefit += benefit;
        if (CollectiveDecisions.Assess(council, proposal, rules, world.Day, proposal.ObservedBenefit / Math.Max(1, proposal.ObservedDays),
            expedition.Phase == "lost" ? .75 : 1, expedition.Phase == "lost" ?
                "Возвращения не было; поселение оценивает решение по истечению ожидаемого срока" :
                "Оценка по доставленному маршруту, новым видам, пригодным участкам и потерям группы"))
            Journal.Record(world, "decision_assessed", proposal.Id, [proposal.CauseEventId, expedition.CauseEventId], new JsonObject
            { ["cityId"] = city.Id, ["outcome"] = proposal.Outcome, ["observedDays"] = proposal.ObservedDays, ["reason"] = proposal.Reason });
    }
}
