using WorldGen.Core.Topology;

namespace WorldGen.Core.Settlements;

public sealed record CellCapacityAllocation(CellAddress Cell, int CapacityUnits);

public sealed record BuildingPlacement(
    string Id,
    string CityId,
    string BuildingTypeId,
    IReadOnlyList<CellCapacityAllocation> Footprint,
    float InfluenceStrength);

public sealed class ConstructionRegistry
{
    private readonly IWorldTopology topology;
    private readonly int baseCapacityPerCell;
    private readonly Dictionary<CellAddress, int> capacityBonuses = new();
    private readonly Dictionary<CellAddress, int> occupiedCapacity = new();
    private readonly Dictionary<string, BuildingPlacement> buildings = new(StringComparer.Ordinal);

    public ConstructionRegistry(IWorldTopology topology, int baseCapacityPerCell = 4)
    {
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        if (baseCapacityPerCell < 1) throw new ArgumentOutOfRangeException(nameof(baseCapacityPerCell));
        this.baseCapacityPerCell = baseCapacityPerCell;
    }

    public IReadOnlyDictionary<string, BuildingPlacement> Buildings => buildings;
    public long Revision { get; private set; }

    public int GetCapacity(CellAddress cell)
    {
        ValidateCell(cell);
        return baseCapacityPerCell + capacityBonuses.GetValueOrDefault(cell);
    }

    public int GetOccupiedCapacity(CellAddress cell)
    {
        ValidateCell(cell);
        return occupiedCapacity.GetValueOrDefault(cell);
    }

    /// <summary>Technology can increase density/floors, but never manufactures additional land cells.</summary>
    public void SetTechnologyCapacityBonus(CellAddress cell, int bonusUnits)
    {
        ValidateCell(cell);
        if (bonusUnits < 0) throw new ArgumentOutOfRangeException(nameof(bonusUnits));
        if (GetOccupiedCapacity(cell) > baseCapacityPerCell + bonusUnits)
            throw new InvalidOperationException("Нельзя уменьшить вместимость ниже уже занятой");
        if (bonusUnits == 0) capacityBonuses.Remove(cell);
        else capacityBonuses[cell] = bonusUnits;
        Revision++;
    }

    public void Place(BuildingPlacement building)
    {
        ArgumentNullException.ThrowIfNull(building);
        if (string.IsNullOrWhiteSpace(building.Id) || string.IsNullOrWhiteSpace(building.CityId) ||
            string.IsNullOrWhiteSpace(building.BuildingTypeId))
            throw new ArgumentException("У постройки должны быть идентификаторы", nameof(building));
        if (buildings.ContainsKey(building.Id)) throw new InvalidOperationException($"Постройка {building.Id} уже существует");
        if (building.Footprint.Count == 0 || building.Footprint.Any(item => item.CapacityUnits < 1))
            throw new ArgumentException("Постройке нужна непустая положительная площадь", nameof(building));
        if (building.Footprint.Select(item => item.Cell).Distinct().Count() != building.Footprint.Count)
            throw new ArgumentException("Одна клетка не должна повторяться в контуре постройки", nameof(building));
        foreach (var allocation in building.Footprint) ValidateCell(allocation.Cell);
        if (!IsConnected(building.Footprint.Select(item => item.Cell).ToHashSet()))
            throw new ArgumentException("Многоклеточная постройка должна иметь связный контур", nameof(building));
        foreach (var allocation in building.Footprint)
        {
            if (GetOccupiedCapacity(allocation.Cell) + allocation.CapacityUnits > GetCapacity(allocation.Cell))
                throw new InvalidOperationException($"В клетке {allocation.Cell} недостаточно строительной вместимости");
        }

        buildings.Add(building.Id, building with { Footprint = Array.AsReadOnly(building.Footprint.ToArray()) });
        foreach (var allocation in building.Footprint)
            occupiedCapacity[allocation.Cell] = GetOccupiedCapacity(allocation.Cell) + allocation.CapacityUnits;
        Revision++;
    }

    public bool Remove(string buildingId)
    {
        if (!buildings.Remove(buildingId, out var building)) return false;
        foreach (var allocation in building.Footprint)
        {
            var occupied = GetOccupiedCapacity(allocation.Cell) - allocation.CapacityUnits;
            if (occupied == 0) occupiedCapacity.Remove(allocation.Cell);
            else occupiedCapacity[allocation.Cell] = occupied;
        }
        Revision++;
        return true;
    }

    public IEnumerable<CityInfluenceSource> ToInfluenceSources() => buildings.Values
        .OrderBy(building => building.Id, StringComparer.Ordinal)
        .SelectMany(building => building.Footprint.Select((allocation, index) => new CityInfluenceSource(
            $"building:{building.Id}:{index}",
            building.CityId,
            allocation.Cell,
            CityAssetKind.Building,
            building.InfluenceStrength * allocation.CapacityUnits / Math.Max(1, building.Footprint.Sum(item => item.CapacityUnits)))));

    private bool IsConnected(HashSet<CellAddress> footprint)
    {
        var visited = new HashSet<CellAddress>();
        var queue = new Queue<CellAddress>();
        queue.Enqueue(footprint.First());
        while (queue.TryDequeue(out var cell))
        {
            if (!visited.Add(cell)) continue;
            foreach (var neighbor in topology.GetNeighbors(cell))
                if (footprint.Contains(neighbor) && !visited.Contains(neighbor)) queue.Enqueue(neighbor);
        }
        return visited.Count == footprint.Count;
    }

    private void ValidateCell(CellAddress cell)
    {
        if (!topology.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
    }
}

public sealed record UsedLandParcel(
    string Id,
    string CityId,
    CellAddress Cell,
    CityAssetKind Kind,
    float Usage,
    float InfluenceStrength)
{
    public CityInfluenceSource ToInfluenceSource()
    {
        if (Kind is not (CityAssetKind.CultivatedField or CityAssetKind.Pasture or CityAssetKind.Orchard))
            throw new InvalidOperationException("Угодье должно быть полем, пастбищем или садом");
        return new CityInfluenceSource($"land:{Id}", CityId, Cell, Kind, InfluenceStrength * Math.Clamp(Usage, 0, 1));
    }
}
