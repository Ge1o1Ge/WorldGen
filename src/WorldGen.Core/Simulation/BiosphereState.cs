using WorldGen.Core.Topology;
namespace WorldGen.Core.Simulation;

public sealed class BiologyState
{
    public HashSet<string> KnownPlants { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> KnownAnimals { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> HarvestedCrops { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CropPlotState> Plots { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CropProductionHistory> CropHistory { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HerdState> Herds { get; set; } = new(StringComparer.Ordinal);
    public List<ResourceCampState> Camps { get; set; } = [];
    public string? LastCampScout { get; set; }
    public double HarvestedTonnes { get; set; }
    public double SeedCollected { get; set; }
    public double CampTimberDelivered { get; set; }
    public string Status { get; set; } = "Знакомство с местными видами";
}
public sealed class CropProductionHistory
{
    public int Seasons { get; set; }
    public int FailedSeasons { get; set; }
    public double ExpectedTonnes { get; set; }
    public double HarvestedTonnes { get; set; }
    public int LastExpectedDay { get; set; } = -1;
    public int LastHarvestDay { get; set; } = -1;
}
public sealed class CropPlotState
{
    public string? CropId { get; set; }
    public double Area { get; set; }
    public double DegreeDays { get; set; }
    public double Health { get; set; } = 1;
    public double AgeDays { get; set; }
    public double HarvestRemaining { get; set; }
    public double TotalHarvested { get; set; }
    public int LastDay { get; set; } = -1;
    public int LastHarvestDay { get; set; } = -1;
    public int FailedSeasons { get; set; }
    public bool SeedSaved { get; set; }
    public bool IsOrchard { get; set; }
    public string? LastFamily { get; set; }
    public double WeatherStress { get; set; }
    public double PestPressure { get; set; }
    public double DiseasePressure { get; set; }
    public string? LastProblem { get; set; }
    public string Phase { get; set; } = "ожидание семян";
}
public sealed class HerdState
{
    public int Females { get; set; }
    public int Males { get; set; }
    public List<YoungAnimals> Young { get; set; } = [];
    public double PregnancyDays { get; set; }
    public double BirthRemainder { get; set; }
    public double Health { get; set; } = 1;
    public double CaptureProgress { get; set; }
    public int Captured { get; set; }
    public int Births { get; set; }
    public int Deaths { get; set; }
    public int Slaughtered { get; set; }
    public int LastBirthDay { get; set; } = -10000;
    public CellAddress? Pasture { get; set; }
    public double PastureWork { get; set; }
    public int PastureStartedDay { get; set; } = -1;
    public double PastureForageConsumed { get; set; }
    public List<CellAddress> PreviousPastures { get; set; } = [];
    public Dictionary<string, double> ProductsToday { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> TotalProducts { get; set; } = new(StringComparer.Ordinal);
    public int LastDay { get; set; } = -1;
    public int Count => Females + Males + Young.Sum(y => y.Count);
}
public sealed record YoungAnimals(int BirthDay, int Count);
public sealed class ResourceCampState
{
    public required string Id { get; init; }
    public required CellAddress Cell { get; init; }
    public required List<CellAddress> Path { get; init; }
    public double Work { get; set; }
    public bool Supplied { get; set; }
    public double Materials { get; set; }
    public int LastUsedDay { get; set; }
    public bool Abandoned { get; set; }
    public double Delivered { get; set; }
}
