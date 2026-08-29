using System.Text.Json.Nodes;
using WorldGen.Core.Topology;
using WorldGen.Core.Settlements;
namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private string? WildAnimal(CellAddress cell)
    {
        if (BiologyRules is not { } rules) return null;
        var point = topology.ToUnitVector(cell); var sample = planetTerrain!.SampleSurface(point);
        return rules.Animals.Select(a => (Animal: a, Score: Biosphere.WildScore(a.Id, a.Habitat, world.Spatial.Grid.Seed, point, sample.TemperatureC, sample.Moisture, sample.ForestCover)))
            .Where(p => p.Score > .1).OrderByDescending(p => p.Score).Select(p => p.Animal.Id).FirstOrDefault();
    }
    private void RegisterPasture(string cityId, string species, CellAddress cell) =>
        layer.UpsertLand(new UsedLandParcel("pasture:" + cityId + ":" + species, cityId, cell, CityAssetKind.Pasture, 1, .28f));
    private double TendSpeciesHerds(CityState city, double available, DailyTelemetry telemetry)
    {
        var r = BiologyRules!; var life = State.Cities[city.Id]; var bio = life.Biology!; var p = life.Primitive!;
        p.HerdCareHours = p.HerdFeedToday = 0;
        if (!Knows(city, "taming")) return 0;
        var origin = Anchor(city); var routesFromHome = Routes(origin); var budget = Math.Min(available, life.LaborAvailableHours * r.AnimalLaborShare); var spent = 0d;
        var reachable = routesFromHome.Cost.Where(p => p.Value < 18 && terrain[p.Key].Terrain != "water").Select(p => p.Key).ToHashSet();
        var wild = (State.Wildlife ?? []).Where(g => g.SpeciesId is not null && reachable.Contains(g.Center) && g.Biomass > 0)
            .OrderBy(g => routesFromHome.Cost[g.Center]).ThenBy(g => g.Id, StringComparer.Ordinal).ToArray();
        foreach (var g in wild) bio.KnownAnimals.Add(g.SpeciesId!);
        foreach (var a in r.Animals)
        {
            if (!bio.Herds.TryGetValue(a.Id, out var herd))
            {
                if (!wild.Any(g => g.SpeciesId == a.Id) || bio.Herds.Values.Count(h => h.Count > 0) >= 3) continue;
                bio.Herds[a.Id] = herd = new();
            }
            if (herd.LastDay == world.Day) continue; herd.LastDay = world.Day;
            herd.ProductsToday.Clear();
            foreach (var young in herd.Young.Where(y => world.Day - y.BirthDay >= a.MaturityDays).ToArray())
            { var females = (young.Count + 1) / 2; herd.Females += females; herd.Males += young.Count - females; herd.Young.Remove(young); }
            var target = Math.Clamp((int)(Population(city) * .003 / a.BodyTonnes), 2, 12);
            var from = herd.Pasture ?? origin;
            if (!routesFromHome.Cost.TryGetValue(from, out var routeCost)) continue;
            var careNeed = herd.Count * a.CareHoursPerDay;
            var feedNeed = (herd.Females + herd.Males + herd.Young.Sum(y => y.Count) * .5) * a.FeedPerDay;
            var waterNeed = herd.Count * a.WaterPerDay;
            var feedCell = herd.Pasture is { } pasture && Stock(terrain[pasture], "forage") > feedNeed ? pasture : BestResourceSite(origin, "forage");
            var transport = herd.Count > 0 ? routeCost * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour : 0;
            var neededHours = careNeed + feedNeed / .004 + transport;
            var coverage = Math.Clamp(Math.Min((budget - spent) / Math.Max(.0001, neededHours), Math.Min(
                city.Stocks["water"] / Math.Max(.000001, waterNeed), feedCell is { } f ? Stock(terrain[f], "forage") / Math.Max(.000001, feedNeed) : 0)), 0, 1);
            var fed = feedCell is { } fc ? Extract(terrain[fc], "forage", feedNeed * coverage) : 0;
            city.Stocks["water"] -= waterNeed * coverage; Add(telemetry.IndustrialConsumptionByResource, "water", waterNeed * coverage);
            var used = neededHours * coverage; spent += used; p.HerdFeedToday += fed;
            if (herd.Count > 0)
            {
                herd.Health = Math.Clamp(herd.Health + (coverage - .85) * .03, 0, 1);
                Add(life.PracticeHours, "animal:" + a.Id, used); Add(life.PracticeHours, "herd", used);
                if (used > 0) { life.Tasks.Add(new("camp:" + city.Id, "herd", from, used, fed)); Passage(routesFromHome.Path(from), 2); }
            }
            if (herd.Health < .25 && herd.Count > 0 && world.Day % 14 == 0)
            {
                if (herd.Young.Count > 0) { var first = herd.Young[0]; herd.Young.RemoveAt(0); if (first.Count > 1) herd.Young.Insert(0, first with { Count = first.Count - 1 }); }
                else if (herd.Males > 0) herd.Males--; else herd.Females--;
                herd.Deaths++; Journal.Record(world, "herd_death", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["species"] = a.Id, ["reason"] = "Недостаток корма, воды или ухода" });
            }
            if (herd.Count > 0 && herd.Pasture is null)
                herd.Pasture = reachable.Where(c => Free(c) && terrain[c].AssignedCityId == city.Id && terrain[c].NaturalState.ForestBiomass < .55)
                    .OrderBy(c => routesFromHome.Cost[c] - terrain[c].NaturalState.SoilQuality * 2).Select(c => (CellAddress?)c).FirstOrDefault();
            if (herd.Pasture is { } land && herd.PastureWork < 24 && budget > spent)
            {
                var setup = Math.Min(budget - spent, 24 - herd.PastureWork); herd.PastureWork += setup; spent += setup;
                life.Tasks.Add(new("camp:" + city.Id, "pasture_prepare", land, setup, 0));
                if (herd.PastureWork >= 24) { RegisterPasture(city.Id, a.Id, land); Journal.Record(world, "pasture_ready", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["species"] = a.Id, ["cell"] = SphericalSimulation.ZoneId(land) }); }
            }
            if (herd.Pasture is { } grazing && herd.PastureWork >= 24 && herd.Count > 0)
            {
                RegisterPasture(city.Id, a.Id, grazing);
                var soil = terrain[grazing].NaturalState;
                soil.SoilQuality = Math.Min(Math.Min(1, terrain[grazing].Fertility + .2), soil.SoilQuality + herd.Count * a.ManurePerDay * coverage);
                if (herd.Females > 0 && herd.Males > 0 && coverage > .95 && herd.Health > .7 && Knows(city, a.Technology) && herd.Count < target * 2)
                {
                    herd.PregnancyDays++;
                    if (herd.PregnancyDays >= a.GestationDays)
                    {
                        herd.PregnancyDays = 0; herd.BirthRemainder += herd.Females * a.Litter;
                        var whole = (int)Math.Floor(herd.BirthRemainder); herd.BirthRemainder -= whole;
                        var born = Math.Min(whole, Math.Max(0, target * 2 - herd.Count));
                        if (born > 0) { herd.Young.Add(new(world.Day, born)); herd.Births += born; herd.LastBirthDay = world.Day; Journal.Record(world, "herd_birth", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["species"] = a.Id, ["count"] = born }); }
                    }
                }
                if (Knows(city, a.Technology)) foreach (var product in a.ProductRules.OrderBy(p => p.ResourceId, StringComparer.Ordinal))
                {
                    if (product.Technology is { } technology && !Knows(city, technology) ||
                        product.LactationDays > 0 && world.Day - herd.LastBirthDay >= product.LactationDays) continue;
                    var potential = herd.Females * product.PerFemalePerDay * coverage;
                    var amount = Math.Min(potential, Math.Max(0, budget - spent) / product.LaborHoursPerUnit);
                    if (amount <= 1e-12) continue;
                    var labor = amount * product.LaborHoursPerUnit; spent += labor;
                    city.Stocks[product.ResourceId] += amount;
                    Add(herd.ProductsToday, product.ResourceId, amount); Add(herd.TotalProducts, product.ResourceId, amount);
                    Add(life.Production, product.ResourceId, amount); Add(telemetry.ProductionByResource, product.ResourceId, amount);
                    var practice = product.Technology is { } id ? Rules.Primitive!.Technologies.Single(t => t.Id == id).Practice : "herd";
                    Add(life.PracticeHours, practice, labor);
                    life.Tasks.Add(new("camp:" + city.Id, "animal_product:" + product.ResourceId, grazing, labor, amount));
                }
            }
            if (budget - spent > 0 && herd.Count < target && city.Stocks["food"] > Population(city) * city.FoodPerPersonPerDay)
            {
                var group = wild.FirstOrDefault(g => g.SpeciesId == a.Id && g.Biomass >= a.BodyTonnes);
                if (group is not null)
                {
                    var travel = routesFromHome.Cost[group.Center] * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
                    var capture = Math.Min(Math.Max(0, budget - spent - travel), Math.Max(0, a.CaptureHours - herd.CaptureProgress));
                    if (capture > 0)
                    {
                        spent += capture + travel; herd.CaptureProgress += capture; Add(life.PracticeHours, "animal:" + a.Id, capture);
                        life.Tasks.Add(new("camp:" + city.Id, "live_capture", group.Center, capture + travel, 0)); Passage(routesFromHome.Path(group.Center), 2);
                    }
                    if (herd.CaptureProgress >= a.CaptureHours)
                    {
                        group.Biomass -= a.BodyTonnes; group.Harvested += a.BodyTonnes; group.Alert = Math.Min(10, group.Alert + .3); group.Threat = origin; group.LastHuntedDay = world.Day;
                        herd.CaptureProgress = 0; if (herd.Captured % 2 == 0) herd.Females++; else herd.Males++; herd.Captured++; herd.Health = Math.Max(.6, herd.Health);
                        Journal.Record(world, "animal_captured", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["species"] = a.Id, ["sourceGroup"] = group.Id });
                    }
                }
            }
            if ((herd.Males > 1 || herd.Females > 2 && herd.Count > target) && budget - spent >= a.BodyTonnes / .01 && (herd.Count > target || city.Stocks["food"] < Population(city) * city.FoodPerPersonPerDay))
            {
                if (herd.Males > 1) herd.Males--; else herd.Females--; herd.Slaughtered++;
                spent += a.BodyTonnes / .01; var meat = a.BodyTonnes * .6; city.Stocks["food"] += meat; city.Stocks["hides"] += a.BodyTonnes * .1;
                RecordFoodProduction(city, "hunt", meat); Add(life.Production, "food", meat); Add(telemetry.ProductionByResource, "food", meat); Add(telemetry.ProductionByResource, "hides", a.BodyTonnes * .1);
            }
            if (herd.Count == 0 && herd.Pasture is not null) layer.SetLandUsage("pasture:" + city.Id + ":" + a.Id, 0);
        }
        p.HerdBiomass = bio.Herds.Sum(h => h.Value.Count * r.Animals.Single(a => a.Id == h.Key).BodyTonnes); p.HerdCareHours = spent;
        return spent;
    }
}
