using System.Text.Json.Nodes;
using WorldGen.Core.Topology;
namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    private bool RemoteWoodPressure(CityState city)
    {
        if (BiologyRules is null) return false;
        var low = city.Stocks["timber"] < Target(city, "timber") * .6 || city.Stocks["firewood"] < Target(city, "firewood") * .5;
        if (!low) return false;
        var nearby = Routes(Anchor(city)).Cost.Where(p => p.Value < 8 && terrain[p.Key].Terrain != "water").Select(p => terrain[p.Key]).ToArray();
        return nearby.Length > 0 && nearby.Max(t => EncounterRate(t, "timber")) < .35;
    }
    private void MaterializeCamp(ResourceCampState camp)
    {
        if (materialize is null) return;
        var area = new HashSet<CellAddress>(camp.Path); var seen = new HashSet<CellAddress> { camp.Cell };
        var queue = new Queue<(CellAddress Cell, int Distance)>(); queue.Enqueue((camp.Cell, 0));
        while (queue.TryDequeue(out var p))
        {
            area.Add(p.Cell); if (p.Distance >= BiologyRules!.CampRadiusCells) continue;
            foreach (var n in topology.GetNeighbors(p.Cell)) if (seen.Add(n) && surveyTerrain!(n).Water == false) queue.Enqueue((n, p.Distance + 1));
        }
        foreach (var cell in area.OrderBy(SphericalSimulation.ZoneId, StringComparer.Ordinal))
        {
            if (terrain.ContainsKey(cell)) continue;
            var patch = materialize(cell); terrain[cell] = patch;
            if (State.Atmosphere is { } sky)
            {
                sky.Ground[patch.Id] = new() { SoilWater = patch.Moisture };
                dailyWeather[cell] = SphericalWeather.Sample(sky, Rules.Primitive!, topology.ToUnitVector(cell), patch.TemperatureC, Math.Max(0, sky.LastDay), world.Calendar.DaysPerYear, sky.Ground[patch.Id]);
            }
        }
        routes.Clear(); cachedWeatherMap = null;
    }
    private double RunResourceCamps(CityState city, double available, DailyTelemetry telemetry)
    {
        if (BiologyRules is not { } r || materialize is null || available <= 0) return 0;
        var life = State.Cities[city.Id]; var bio = life.Biology!; var budget = Math.Min(available, life.LaborAvailableHours * r.CampWorkerShare); var spent = 0d;
        var scout = State.Scouting?.Expeditions.FirstOrDefault(e => e.CityId == city.Id && e.Phase == "returned");
        if (RemoteWoodPressure(city) && scout is not null && bio.LastCampScout != scout.Id && bio.Camps.Count(c => !c.Abandoned) < r.MaximumCampsPerCity)
        {
            bio.LastCampScout = scout.Id;
            var candidates = scout.Path.Select((cell, index) => (Cell: cell, Index: index))
                .Where(p => p.Index > 10 && !surveyTerrain!(p.Cell).Water && (!terrain.TryGetValue(p.Cell, out var t) || t.AssignedCityId is "" || t.AssignedCityId == city.Id))
                .Where(p => bio.Camps.All(c => c.Cell != p.Cell))
                .OrderByDescending(p => (terrain.TryGetValue(p.Cell, out var t) ? t.NaturalState.ForestBiomass : surveyTerrain!(p.Cell).Forest) / (1 + p.Index * .025)).ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates[0];
                if ((terrain.TryGetValue(selected.Cell, out var t) ? t.NaturalState.ForestBiomass : surveyTerrain!(selected.Cell).Forest) > .15)
                {
                    bio.Camps.Add(new() { Id = $"resource-camp:{city.Id}:{bio.Camps.Count + 1}", Cell = selected.Cell, Path = scout.Path.Take(selected.Index + 1).ToList(), LastUsedDay = world.Day });
                    Journal.Record(world, "resource_camp_planned", city.Id, details: new JsonObject { ["cityId"] = city.Id, ["scout"] = scout.Id, ["cell"] = SphericalSimulation.ZoneId(selected.Cell), ["reason"] = "Местный лес истощён; разведчики сообщили о дальнем участке" });
                }
            }
        }
        foreach (var camp in bio.Camps.Where(c => !c.Abandoned))
        {
            if (spent >= budget) break;
            if (camp.Work < r.CampSetupHours)
            {
                if (!camp.Supplied)
                {
                    var issued = Math.Min(city.Stocks["timber"], Math.Max(0, r.CampTimber - camp.Materials));
                    city.Stocks["timber"] -= issued; camp.Materials += issued; Add(telemetry.InfrastructureConsumptionByResource, "timber", issued);
                    if (camp.Materials < r.CampTimber)
                    {
                        // A paid trip can make a stick shelter on site even if the town has no timber left.
                        MaterializeCamp(camp);
                        var setupTravel = camp.Path.Count * 2 * world.Spatial.Grid.ZoneSizeMeters / Rules.WalkingMetersPerHour;
                        var collected = Math.Min(r.CampTimber - camp.Materials, Math.Max(0, budget - spent - setupTravel) * .0008);
                        collected = Extract(terrain[camp.Cell], "timber", collected);
                        if (collected > 0)
                        {
                            var materialHours = setupTravel + collected / .0008; spent += materialHours; camp.Materials += collected;
                            Add(telemetry.ProductionByResource, "timber", collected); Add(telemetry.InfrastructureConsumptionByResource, "timber", collected);
                            life.Tasks.Add(new("camp:" + city.Id, "camp_materials", camp.Cell, materialHours, collected)); Passage(camp.Path, 2);
                        }
                    }
                    if (camp.Materials + 1e-9 < r.CampTimber) continue;
                    camp.Supplied = true;
                }
                var work = Math.Min(budget - spent, r.CampSetupHours - camp.Work); camp.Work += work; spent += work;
                life.Tasks.Add(new("camp:" + city.Id, "camp_setup", camp.Cell, work, 0));
                if (camp.Work >= r.CampSetupHours) { MaterializeCamp(camp); Journal.Record(world, "resource_camp_ready", camp.Id, details: new JsonObject { ["cityId"] = city.Id, ["cell"] = SphericalSimulation.ZoneId(camp.Cell) }); }
                continue;
            }
            var origin = camp.Path[0];
            if (camp.Path.Any(c => surveyTerrain!(c).Water)) { bio.Status = "Путь к промысловому лагерю недоступен"; continue; }
            var candidates = Routes(camp.Cell).Cost.Where(p => p.Value <= r.CampRadiusCells * 1.7 && terrain[p.Key].Terrain != "water" && layer.Construction.GetOccupiedCapacity(p.Key) == 0)
                .OrderByDescending(p => EncounterRate(terrain[p.Key], "timber") / (1 + p.Value * .1)).ToArray();
            var best = candidates.FirstOrDefault();
            if (candidates.Length == 0 || Stock(terrain[best.Key], "timber") < .001)
            {
                if (world.Day - camp.LastUsedDay > 90) { camp.Abandoned = true; Journal.Record(world, "resource_camp_abandoned", camp.Id, details: new JsonObject { ["cityId"] = city.Id, ["reason"] = "Доступная древесина больше не оправдывает промысел" }); }
                continue;
            }
            var timberMissing = Math.Max(0, Target(city, "timber") - city.Stocks["timber"]);
            var fuelMissing = Math.Max(0, Target(city, "firewood") - city.Stocks["firewood"]);
            if (timberMissing + fuelMissing <= 0) continue;
            var woodRate = .0012 * (Knows(city, "woodworking") ? 1.6 : .65) * (.25 + .75 * KitCoverage(city)) * EncounterRate(terrain[best.Key], "timber");
            var travel = camp.Path.Skip(1).Sum(c => WeatherWalking(c)) * world.Spatial.Grid.ZoneSizeMeters * 2 / Rules.WalkingMetersPerHour;
            var costPerTonne = 1 / Math.Max(.000001, woodRate) + travel / r.CampCarryTonnesPerHour;
            var amount = Math.Min(timberMissing + fuelMissing, Math.Max(0, budget - spent - r.CampDailyHours) / costPerTonne);
            amount = Extract(terrain[best.Key], "timber", amount); if (amount <= 0) continue;
            var timber = Math.Min(timberMissing, amount); city.Stocks["timber"] += timber; city.Stocks["firewood"] += amount - timber;
            Add(life.Production, "timber", timber); Add(life.Production, "firewood", amount - timber); Add(telemetry.ProductionByResource, "timber", timber); Add(telemetry.ProductionByResource, "firewood", amount - timber);
            var hours = r.CampDailyHours + amount * costPerTonne; spent += hours; camp.LastUsedDay = world.Day; camp.Delivered += amount; bio.CampTimberDelivered += amount;
            Add(life.PracticeHours, "wood", hours); life.Tasks.Add(new("camp:" + city.Id, "remote_wood", best.Key, hours, amount)); Passage(camp.Path, 2);
        }
        return spent;
    }
}
