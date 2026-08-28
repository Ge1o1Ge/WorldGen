using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

/// <summary>Scenario anchors are preferred regions, not permission to spawn underwater.
/// Deterministic coarse search keeps the same world/seed reproducible.</summary>
public static class PrimitiveStartLocations
{
    public static SphericalWorldDefinition Resolve(SphericalWorldDefinition definition, SphericalTerrainGenerator generator, SphericalHydrology hydro)
    {
        var topology = new CubeSphereTopology(definition.FaceSize);
        var candidates = new List<(CellAddress Cell, UnitVector3 Point, double Quality)>();
        var step = Math.Max(1, definition.FaceSize / 80);
        foreach (var face in Enum.GetValues<CubeFace>())
        for (var y = step; y < definition.FaceSize - step; y += step)
        for (var x = step; x < definition.FaceSize - step; x += step)
        {
            var cell = new CellAddress(face, x, y); var point = topology.ToUnitVector(cell);
            var value = generator.GenerateCell(cell);
            if (value.Biome == SphericalBiome.Ocean || value.Fertility < .25 || value.ForestCover < .12) continue;
            var hc = hydro.Topology.Locate(point); var i = hydro.Index(hc);
            if (hydro.IsWater(i)) continue;
            bool Fresh(CellAddress a) => hydro.IsFreshWater(hydro.Index(a));
            if (!Fresh(hc) && !hydro.GetDrainageNeighbors(hc).Any(Fresh)) continue;
            if (topology.GetNeighbors(cell).Any(n => generator.GenerateCell(n).Biome == SphericalBiome.Ocean)) continue;
            candidates.Add((cell, point, value.Fertility * .1 + value.ForestCover * .03));
        }
        var used = new List<UnitVector3>();
        var settlements = new List<SphericalSettlementDefinition>();
        foreach (var settlement in definition.Settlements)
        {
            var desired = settlement.Buildings[0].Footprint[0];
            var point = topology.ToUnitVector(new(desired.Face, desired.X, desired.Y));
            var eligible = candidates.Where(c => used.All(p => p.Dot(c.Point) < .94)).ToArray();
            if (eligible.Length == 0) throw new InvalidOperationException("Недостаточно разных пригодных мест у пресной воды");
            var chosen = eligible.OrderByDescending(c => c.Point.Dot(point) + c.Quality).ThenBy(c => SphericalSimulation.ZoneId(c.Cell), StringComparer.Ordinal).First();
            used.Add(chosen.Point);
            settlements.Add(settlement with { Buildings = [settlement.Buildings[0] with { Footprint = [desired with { Face = chosen.Cell.Face, X = chosen.Cell.X, Y = chosen.Cell.Y }] }] });
        }
        return definition with { Settlements = settlements };
    }
}
