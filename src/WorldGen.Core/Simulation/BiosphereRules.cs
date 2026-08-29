using System.Text.Json.Serialization;
using WorldGen.Core.Content;
using WorldGen.Core.Topology;

namespace WorldGen.Core.Simulation;

public sealed record HabitatRule(double MinTemperature, double MaxTemperature, double MinMoisture, double MaxMoisture,
    double MinForest = 0, double MaxForest = 1);
public sealed record CropRule(string Id, string Name, string Group, string Symbol, HabitatRule Habitat,
    double BaseTemperature, double FrostTolerance, double DegreeDays, double SeedTonnes, double YieldTonnes,
    double PlantHours, double CareHours, double HarvestTonnesPerHour, double SeedShare,
    double FoodValue = 1, double MatureYears = 0, double StorageDecay = .002, string Family = "other")
{
    [JsonIgnore] public string Technology => "grow_" + Id;
    [JsonIgnore] public string SeedResource => "seed_" + Id;
    [JsonIgnore] public string HarvestResource => "harvest_" + Id;
}
public sealed record AnimalProductRule(string ResourceId, string Name, string Unit, string Category, string? Technology,
    double PerFemalePerDay, double LaborHoursPerUnit, double DecayPerDay, double FoodValue = 0, int LactationDays = 0);
public sealed record AnimalRule(string Id, string Name, string Symbol, HabitatRule Habitat,
    double BodyTonnes, double CaptureHours, double FeedPerDay, double WaterPerDay, double CareHoursPerDay,
    int MaturityDays, int GestationDays, double Litter, double ManurePerDay = .00001, AnimalProductRule[]? Products = null)
{
    [JsonIgnore] public string Technology => "herd_" + Id;
    [JsonIgnore] public IReadOnlyList<AnimalProductRule> ProductRules => Products ?? [];
}
public sealed record BiosphereRules
{
    public int Version { get; init; } = 1;
    public required CropRule[] Crops { get; init; }
    public required AnimalRule[] Animals { get; init; }
    public double SearchHoursPerDay { get; init; } = 2;
    public double SeedTonnesPerSearchHour { get; init; } = .00008;
    public double FarmingLaborShare { get; init; } = .38;
    public double AnimalLaborShare { get; init; } = .08;
    public double FoodProcessingTonnesPerHour { get; init; } = .012;
    public int RotationCropCount { get; init; } = 3;
    public int SurveyIntervalDays { get; init; } = 14;
    public int CampRadiusCells { get; init; } = 5;
    public int MaximumCampsPerCity { get; init; } = 4;
    public double CampSetupHours { get; init; } = 32;
    public double CampTimber { get; init; } = .03;
    public double CampDailyHours { get; init; } = 1;
    public double CampCarryTonnesPerHour { get; init; } = .025;
    public double CampWorkerShare { get; init; } = .08;
    public void Validate()
    {
        static bool Habitat(HabitatRule h) => double.IsFinite(h.MinTemperature + h.MaxTemperature + h.MinMoisture + h.MaxMoisture + h.MinForest + h.MaxForest) &&
            h.MinTemperature < h.MaxTemperature && h.MinMoisture >= 0 && h.MinMoisture < h.MaxMoisture && h.MaxMoisture <= 1 && h.MinForest >= 0 && h.MinForest <= h.MaxForest && h.MaxForest <= 1;
        var ids = Crops.Select(c => c.Id).Concat(Animals.Select(a => a.Id)).ToArray();
        if (Version != 1 || ids.Length != ids.Distinct(StringComparer.Ordinal).Count() || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) ||
            Crops.Any(c => !Habitat(c.Habitat) || string.IsNullOrWhiteSpace(c.Symbol) || !double.IsFinite(c.DegreeDays + c.SeedTonnes + c.YieldTonnes + c.PlantHours + c.CareHours + c.SeedShare + c.FrostTolerance + c.MatureYears + c.FoodValue + c.BaseTemperature + c.StorageDecay + c.HarvestTonnesPerHour) ||
                c.DegreeDays <= 0 || c.SeedTonnes <= 0 || c.YieldTonnes <= c.SeedTonnes || c.PlantHours <= 0 || c.CareHours < 0 || c.HarvestTonnesPerHour <= 0 || c.SeedShare <= 0 || c.SeedShare >= .5 || c.MatureYears < 0 || c.FoodValue <= 0 || c.StorageDecay < 0 || c.StorageDecay >= 1) ||
            Animals.Any(a => !Habitat(a.Habitat) || !double.IsFinite(a.BodyTonnes + a.CaptureHours + a.FeedPerDay + a.WaterPerDay + a.CareHoursPerDay + a.Litter + a.ManurePerDay) ||
                a.BodyTonnes <= 0 || a.CaptureHours <= 0 || a.FeedPerDay <= 0 || a.WaterPerDay <= 0 || a.CareHoursPerDay <= 0 || a.MaturityDays < 1 || a.GestationDays < 1 || a.Litter <= 0 || a.ManurePerDay < 0 ||
                a.ProductRules.Any(p => string.IsNullOrWhiteSpace(p.ResourceId) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Unit) || string.IsNullOrWhiteSpace(p.Category) ||
                    p.Technology is { Length: 0 } || !double.IsFinite(p.PerFemalePerDay + p.LaborHoursPerUnit + p.DecayPerDay + p.FoodValue) ||
                    p.PerFemalePerDay <= 0 || p.LaborHoursPerUnit <= 0 || p.DecayPerDay < 0 || p.DecayPerDay >= 1 || p.FoodValue < 0 || p.LactationDays < 0)) ||
            FarmingLaborShare is <= 0 or > .6 || AnimalLaborShare is <= 0 or > .3 || SearchHoursPerDay <= 0 || SeedTonnesPerSearchHour <= 0 || FoodProcessingTonnesPerHour <= 0 ||
            RotationCropCount < 2 || RotationCropCount > Crops.Length || SurveyIntervalDays < 1 || CampRadiusCells is < 1 or > 8 || MaximumCampsPerCity is < 1 or > 8 ||
            !double.IsFinite(SearchHoursPerDay + SeedTonnesPerSearchHour + FarmingLaborShare + AnimalLaborShare + FoodProcessingTonnesPerHour + CampSetupHours + CampTimber + CampDailyHours + CampCarryTonnesPerHour + CampWorkerShare) ||
            CampSetupHours <= 0 || CampTimber < 0 || CampDailyHours <= 0 || CampCarryTonnesPerHour <= 0 || CampWorkerShare is <= 0 or > .25)
            throw new InvalidOperationException("Некорректный каталог биосферы");
    }
    public IEnumerable<PrimitiveTechnologyRule> Technologies() =>
        Crops.Select(c => new PrimitiveTechnologyRule(c.Technology, "Выращивание: " + c.Name, "food", false,
            c.MatureYears > 0 ? ["horticulture"] : ["gardening"], "seed:" + c.Id, 30))
        .Concat(Animals.Select(a => new PrimitiveTechnologyRule(a.Technology, "Разведение: " + a.Name, "food", false, ["taming"], "animal:" + a.Id, 80)))
        .Append(new("crop_rotation", "Севооборот", "food", false, ["gardening", "seed_selection"], "cultivate", 600));
    public IEnumerable<ResourceDefinition> Resources() => Crops.SelectMany(c => new[] {
        new ResourceDefinition {Id=c.SeedResource,Name="Посадочный материал: "+c.Name,Unit="тонна",Category="seed",BaseValue=2,DecayPerDay=.0003},
        new ResourceDefinition {Id=c.HarvestResource,Name=c.Name,Unit="тонна",Category="crop",BaseValue=c.FoodValue,DecayPerDay=c.StorageDecay}})
        .Concat(Animals.SelectMany(a => a.ProductRules).GroupBy(p => p.ResourceId, StringComparer.Ordinal).Select(group =>
        {
            var product = group.First();
            if (group.Any(p => p.Name != product.Name || p.Unit != product.Unit || p.Category != product.Category ||
                p.DecayPerDay != product.DecayPerDay || p.FoodValue != product.FoodValue))
                throw new InvalidOperationException($"Несогласованное описание животного продукта {group.Key}");
            return new ResourceDefinition { Id = product.ResourceId, Name = product.Name, Unit = product.Unit,
                Category = product.Category, BaseValue = Math.Max(1, product.FoodValue), DecayPerDay = product.DecayPerDay, FoodValue = product.FoodValue };
        }));
}

public static class Biosphere
{
    public static double Suitability(HabitatRule h, double temperature, double moisture, double forest)
    {
        if (temperature < h.MinTemperature || temperature > h.MaxTemperature || moisture < h.MinMoisture || moisture > h.MaxMoisture || forest < h.MinForest || forest > h.MaxForest) return 0;
        return Math.Clamp(Math.Min((temperature - h.MinTemperature + 2) / 5, (h.MaxTemperature - temperature + 2) / 5), .1, 1) *
            Math.Clamp(Math.Min((moisture - h.MinMoisture + .08) / .2, (h.MaxMoisture - moisture + .08) / .2), .1, 1);
    }
    public static double Presence(string id, uint seed, UnitVector3 p)
    {
        uint hash = 2166136261 ^ seed; foreach (var c in id) hash = unchecked((hash ^ c) * 16777619);
        var phase = (hash % 10000) / 1000d;
        // Broad geographic ranges, not independent per-cell dice or a latitude seam.
        return Math.Clamp((Math.Sin(p.X * 7 + phase) + Math.Cos(p.Z * 6 + phase * 2) + Math.Sin(p.Y * 5 - phase) + .65) / 2.6, 0, 1);
    }
    public static double WildScore(string id, HabitatRule habitat, uint seed, UnitVector3 p, double temperature, double moisture, double forest) =>
        Suitability(habitat, temperature, moisture, forest) * Presence(id, seed, p);
    public static double Growth(CropRule crop, double temperature, double soilWater, double snow) =>
        snow > 5 || temperature < crop.FrostTolerance ? 0 : Math.Max(0, temperature - crop.BaseTemperature) * Math.Clamp(soilWater / Math.Max(.1, crop.Habitat.MinMoisture), 0, 1);
}
