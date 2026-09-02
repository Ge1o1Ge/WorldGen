using WorldGen.Core.Spatial;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed partial class SettlementSimulation
{
    // One persistent stock per mobile group; these weights only project it onto
    // its current habitat. Overlapping zones never manufacture a second stock.
    private readonly Dictionary<CellAddress, List<(WildlifeGroupState Group, double Weight)>> wildlifeIndex = new();
    private readonly Dictionary<CellAddress, (bool Land, double Forest)> wildlifeTerrain = new();

    private (bool Land, double Forest) WildlifeTerrain(CellAddress cell)
    {
        if (terrain.TryGetValue(cell, out var t)) return (!OpenWater(cell) && layer.Construction.GetOccupiedCapacity(cell) == 0, t.NaturalState.ForestBiomass);
        if (wildlifeTerrain.TryGetValue(cell, out var cached)) return cached;
        var sample = surveyTerrain?.Invoke(cell);
        var value = (sample is { Water: false }, sample?.Forest ?? 0);
        if (wildlifeTerrain.Count >= 16384) wildlifeTerrain.Clear();
        return wildlifeTerrain[cell] = value;
    }
    private void InitializeWildlife()
    {
        if (State.Wildlife is null)
        {
            var groups = new List<WildlifeGroupState>(); var size = Rules.Subsistence!.Wildlife.SeedPatchSize;
            // Each initial component is connected even where a sea cuts a patch.
            foreach (var patch in terrain.Where(p => !OpenWater(p.Key) && p.Value.ForestCover > 0)
                .GroupBy(p => (p.Key.Face, X: p.Key.X / size, Y: p.Key.Y / size)).OrderBy(g => g.Key.Face).ThenBy(g => g.Key.X).ThenBy(g => g.Key.Y))
            {
                var remaining = patch.Select(p => p.Key).ToHashSet();
                while (remaining.Count > 0)
                {
                    var first = remaining.OrderBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).First();
                    var component = new List<CellAddress>(); var queue = new Queue<CellAddress>(); queue.Enqueue(first); remaining.Remove(first);
                    while (queue.TryDequeue(out var cell))
                    {
                        component.Add(cell);
                        foreach (var neighbor in topology.GetNeighbors(cell)) if (remaining.Remove(neighbor)) queue.Enqueue(neighbor);
                    }
                    var center = component.Where(c => WildlifeTerrain(c).Land).OrderBy(c => Math.Abs(c.X - component.Average(p => p.X)) + Math.Abs(c.Y - component.Average(p => p.Y)))
                        .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).DefaultIfEmpty(first).First();
                    groups.Add(new()
                    {
                        Id = $"wildlife-{groups.Count + 1:0000}",
                        SpeciesId = WildAnimal(center),
                        Center = center,
                        PreviousCenter = center,
                        RadiusCells = Rules.Subsistence.Wildlife.RangeRadiusCells,
                        Capacity = component.Sum(c => pools["game"].Capacity * terrain[c].ForestCover),
                        Biomass = component.Sum(c => Stock(terrain[c], "game"))
                    });
                }
            }
            // Migration transfers, not copies, old patch biomass.
            foreach (var stocks in State.WildStocks.Values) stocks.Remove("game");
            State.Wildlife = groups;
        }
        RebuildWildlifeIndex();
    }
    private IReadOnlyList<CellAddress> WildlifeFootprint(WildlifeGroupState group)
    {
        var cells = new List<CellAddress>(); var seen = new HashSet<CellAddress> { group.Center };
        var queue = new Queue<(CellAddress Cell, int Distance)>(); queue.Enqueue((group.Center, 0));
        while (queue.TryDequeue(out var next))
        {
            var habitat = WildlifeTerrain(next.Cell);
            if (habitat.Land && habitat.Forest > .02) cells.Add(next.Cell);
            if (next.Distance >= group.RadiusCells) continue;
            foreach (var cell in topology.GetNeighbors(next.Cell))
                if (seen.Add(cell) && WildlifeTerrain(cell).Land) queue.Enqueue((cell, next.Distance + 1));
        }
        return cells;
    }
    private void RebuildWildlifeIndex()
    {
        wildlifeIndex.Clear();
        foreach (var group in State.Wildlife ?? [])
        {
            var footprint = WildlifeFootprint(group); var total = footprint.Sum(c => WildlifeTerrain(c).Forest);
            if (total <= 0) continue;
            foreach (var cell in footprint)
            {
                if (!wildlifeIndex.TryGetValue(cell, out var present)) wildlifeIndex[cell] = present = [];
                present.Add((group, WildlifeTerrain(cell).Forest / total));
            }
        }
    }
    private double WildlifeAt(Territory t, bool capacity = false) => wildlifeIndex.TryGetValue(addresses[t.Id], out var groups)
        ? groups.Sum(p => (capacity ? p.Group.Capacity : p.Group.Biomass) * p.Weight) : 0;
    private double WildlifeAlert(Territory t) => wildlifeIndex.TryGetValue(addresses[t.Id], out var groups)
        ? groups.Sum(p => p.Group.Alert * p.Group.Biomass * p.Weight) / Math.Max(1e-9, WildlifeAt(t)) : 0;
    private double HuntWildlife(Territory t, double requested, CellAddress hunter)
    {
        if (!wildlifeIndex.TryGetValue(addresses[t.Id], out var groups)) return 0;
        var available = WildlifeAt(t); if (available <= 0) return 0;
        var taken = Math.Min(available, requested);
        foreach (var (group, weight) in groups)
        {
            var amount = taken * group.Biomass * weight / available;
            group.Biomass = Math.Max(0, group.Biomass - amount); group.Harvested += amount;
            if (amount <= 0) continue;
            group.Alert = Math.Min(10, group.Alert + amount / Rules.Subsistence!.EasyCatchTonnes["game"]);
            group.Threat = hunter; group.LastHuntedDay = world.Day;
        }
        return taken;
    }
    private void AdvanceWildlife()
    {
        if (State.Wildlife is null || Rules.Subsistence is not { } subsistence) return;
        var rules = subsistence.Wildlife;
        for (var i = 0; i < State.Wildlife.Count; i++)
        {
            var group = State.Wildlife[i];
            group.Alert *= Math.Pow(.5, 1 / rules.AlertHalfLifeDays);
            if (group.Alert < rules.FleeThreshold * .2 &&
                group.LastHuntedDay >= 0 && world.Day - group.LastHuntedDay > rules.AlertHalfLifeDays * 2)
                group.Threat = null;
            var habitat = WildlifeTerrain(group.Center);
            var growth = Math.Max(0, group.Capacity - group.Biomass) * RecoveryRate("game") * (habitat.Land ? Math.Clamp(habitat.Forest * 2, 0, 1) : 0) * WeatherGrowth(group.Center);
            group.Biomass += growth; group.Regrown += growth;
            var fleeing = group.Alert >= rules.FleeThreshold && group.Threat is not null;
            if (!fleeing && habitat.Land && (world.Day + i) % rules.QuietMoveIntervalDays != 0) continue;
            double Distance(CellAddress cell)
            {
                if (group.Threat is not { } threat) return 0;
                var a = topology.ToUnitVector(cell); var b = topology.ToUnitVector(threat);
                return ((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z)) * topology.FaceSize * topology.FaceSize;
            }
            var candidates = topology.GetNeighbors(group.Center).Where(c => WildlifeTerrain(c) is { Land: true, Forest: > .02 }).ToArray();
            var currentDistance = Distance(group.Center);
            if (fleeing) candidates = candidates.Where(c => Distance(c) > currentDistance + 1e-9).ToArray();
            if (candidates.Length == 0) continue; // No teleporting through water or blocked terrain.
            // A calm group keeps some momentum and avoids immediately stepping
            // back to the cell it just left. The previous deterministic
            // "best forest, then id" rule made herds oscillate between two
            // cells and look stationary on the map.
            var quietCandidates = candidates.Length > 1
                ? candidates.Where(c => c != group.PreviousCenter).ToArray()
                : candidates;
            if (quietCandidates.Length == 0) quietCandidates = candidates;
            var heading = group.PreviousCenter == group.Center ? (UnitVector3?)null : UnitVector3.Normalize(
                topology.ToUnitVector(group.Center).X - topology.ToUnitVector(group.PreviousCenter).X,
                topology.ToUnitVector(group.Center).Y - topology.ToUnitVector(group.PreviousCenter).Y,
                topology.ToUnitVector(group.Center).Z - topology.ToUnitVector(group.PreviousCenter).Z);
            double QuietScore(CellAddress cell)
            {
                var momentum = heading is { } direction ? StepAlignment(group.Center, cell, direction) : 0;
                var habitatScore = Math.Clamp(WildlifeTerrain(cell).Forest, 0, 1);
                var variation = StableRoll(group.Id + ":wander", world.Day / Math.Max(1, rules.QuietMoveIntervalDays),
                    (int)cell.Face * 1000000 + cell.Y * topology.FaceSize + cell.X);
                return habitatScore * .62 + momentum * .28 + variation * .10;
            }
            var next = quietCandidates.OrderByDescending(c => fleeing ? Distance(c) : QuietScore(c))
                .ThenBy(SphericalSimulation.ZoneId, StringComparer.Ordinal).First();
            group.PreviousCenter = group.Center; group.Center = next; group.LastMoveDay = world.Day; group.Moves++;
        }
        RebuildWildlifeIndex();
    }
}
