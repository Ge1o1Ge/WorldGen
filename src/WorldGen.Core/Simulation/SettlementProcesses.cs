using WorldGen.Core.Content;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    /// <summary>
    /// Runs data-defined household and workshop-scale transformations. Unlike the
    /// regional industry list these processes appear organically when a settlement
    /// knows the technology and has real inputs, equipment and spare labour.
    /// </summary>
    private double RunPrimitiveProcesses(CityState city, double available, DailyTelemetry telemetry)
    {
        if (Rules.Primitive is not { Processes.Length: > 0 } primitive || available <= 0) return 0;
        var life = State.Cities[city.Id];
        var spent = 0d;
        foreach (var rule in primitive.Processes.OrderByDescending(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!life.Processes.TryGetValue(rule.Id, out var state)) life.Processes[rule.Id] = state = new();
            state.LastDay = world.Day; state.BatchesToday = state.LaborHoursToday = 0; state.Constraint = state.BuildingId = null; state.LaborMultiplier = 1;
            if (!Knows(city, rule.Technology)) { state.Constraint = "technology:" + rule.Technology; continue; }
            var building = rule.BuildingRequirements.Length == 0 ? null : State.Buildings
                .Where(candidate => candidate.CityId == city.Id && candidate.Status == "active" && rule.BuildingRequirements.Contains(candidate.Kind, StringComparer.Ordinal))
                .OrderBy(candidate => rule.LaborMultipliers.GetValueOrDefault(candidate.Kind, 1) / Math.Max(.05, Efficiency(candidate)))
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal).FirstOrDefault();
            if (rule.BuildingRequirements.Length > 0 && building is null)
            {
                state.Constraint = "building:any:" + string.Join('|', rule.BuildingRequirements.Order(StringComparer.Ordinal));
                continue;
            }
            if (building is not null)
            {
                state.BuildingId = building.Id;
                state.LaborMultiplier = rule.LaborMultipliers.GetValueOrDefault(building.Kind, 1) / Math.Max(.05, Efficiency(building));
            }
            var missingEquipment = rule.RequiredStocks.OrderBy(p => p.Key, StringComparer.Ordinal)
                .FirstOrDefault(pair => city.Stocks.GetValueOrDefault(pair.Key) + 1e-9 < pair.Value);
            if (!string.IsNullOrEmpty(missingEquipment.Key)) { state.Constraint = "equipment:" + missingEquipment.Key; continue; }

            var targetOutputPerBatch = rule.Outputs[rule.TargetResource];
            var missing = Math.Max(0, Population(city) * rule.TargetOutputPerPerson - city.Stocks.GetValueOrDefault(rule.TargetResource));
            if (missing <= 1e-9) continue;
            var planned = Math.Min(Population(city) * rule.MaximumBatchesPerPersonPerDay, missing / targetOutputPerBatch);
            var laborPerBatch = rule.LaborHoursPerBatch * state.LaborMultiplier;
            var laborLimit = Math.Max(0, available - spent) / laborPerBatch;
            var inputLimit = rule.Inputs.Min(pair => city.Stocks.GetValueOrDefault(pair.Key) / pair.Value);
            var batches = SimulationMath.Quantize(Math.Max(0, Math.Min(planned, Math.Min(laborLimit, inputLimit))));
            if (batches <= 1e-9)
            {
                state.Constraint = laborLimit <= 1e-9 ? "labor" : "input:" + rule.Inputs.OrderBy(pair =>
                    city.Stocks.GetValueOrDefault(pair.Key) / pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
                continue;
            }
            foreach (var input in rule.Inputs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var amount = SimulationMath.Quantize(input.Value * batches);
                city.Stocks[input.Key] = SimulationMath.Quantize(Math.Max(0, city.Stocks.GetValueOrDefault(input.Key) - amount));
                Add(telemetry.IndustrialConsumptionByResource, input.Key, amount);
            }
            foreach (var produced in rule.Outputs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var amount = SimulationMath.Quantize(produced.Value * batches);
                city.Stocks[produced.Key] = SimulationMath.Quantize(city.Stocks.GetValueOrDefault(produced.Key) + amount);
                Add(life.Production, produced.Key, amount); Add(telemetry.ProductionByResource, produced.Key, amount);
            }
            var labor = SimulationMath.Quantize(laborPerBatch * batches);
            spent += labor; state.BatchesToday = batches; state.LaborHoursToday = labor;
            state.TotalBatches = SimulationMath.Quantize(state.TotalBatches + batches);
            Add(life.PracticeHours, rule.Practice, labor);
            life.Tasks.Add(new(building?.Id ?? "workshop:" + city.Id, "process:" + rule.Id, building?.Cell ?? Anchor(city), labor, batches));
            if (batches + 1e-9 < planned)
                state.Constraint = laborLimit <= inputLimit ? "labor" : "input:" + rule.Inputs.OrderBy(pair =>
                    city.Stocks.GetValueOrDefault(pair.Key) / pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
        }
        return spent;
    }

    private IEnumerable<ResourceDefinition> DirectFoodResources() => content.Resources.Resources
        .Where(resource => resource.FoodValue > 0 && resource.Id is not "food" and not "winter_food")
        .OrderByDescending(resource => resource.DecayPerDay).ThenBy(resource => resource.Id, StringComparer.Ordinal);

    private double EdibleFoodEquivalent(CityState city) => DirectFoodResources()
        .Sum(resource => city.Stocks.GetValueOrDefault(resource.Id) * resource.FoodValue);

    public double ConsumeEdibleStocks(CityState city, double requestedFoodEquivalent, DailyTelemetry telemetry)
    {
        var remaining = Math.Max(0, requestedFoodEquivalent); var consumed = 0d;
        foreach (var resource in DirectFoodResources())
        {
            var units = Math.Min(city.Stocks.GetValueOrDefault(resource.Id), remaining / resource.FoodValue);
            if (units <= 1e-12) continue;
            var equivalent = SimulationMath.Quantize(units * resource.FoodValue);
            city.Stocks[resource.Id] = SimulationMath.Quantize(Math.Max(0, city.Stocks[resource.Id] - units));
            Add(telemetry.HouseholdConsumptionByResource, resource.Id, units);
            RecordDirectFoodConsumption(city, resource.Id, equivalent);
            remaining = Math.Max(0, remaining - equivalent); consumed += equivalent;
            if (remaining <= 1e-9) break;
        }
        return SimulationMath.Quantize(consumed);
    }
}
