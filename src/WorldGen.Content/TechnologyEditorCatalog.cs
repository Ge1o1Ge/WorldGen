using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorldGen.Core.Content;
using WorldGen.Core.Simulation;

namespace WorldGen.Content;

public sealed record TechnologyEditorNode(string Id, string TechnologyId, string Title, string Domain,
    string Layer, string Source, string Description, string[] Conditions, string[] Effects, string? Symbol);
public sealed record TechnologyEditorEdge(string Id, string From, string To, string Type);
public sealed record TechnologyAnnotation(string Description, string[] Effects, string? Symbol);
public sealed record TechnologyEditorCatalog(TechnologyEditorNode[] Nodes, TechnologyEditorEdge[] Edges)
{
    public string Version => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, HashOptions))));
    private static readonly JsonSerializerOptions HashOptions = new() { IgnoreReadOnlyProperties = true };

    public static TechnologyEditorCatalog Build(ContentCatalog content, PrimitiveWorldRules? primitive,
        IReadOnlyDictionary<string, TechnologyAnnotation>? annotations = null)
    {
        var nodes = new List<TechnologyEditorNode>();
        var edges = new List<TechnologyEditorEdge>();
        static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        foreach (var tech in primitive?.Technologies ?? [])
        {
            var crop = primitive!.Biosphere?.Crops.FirstOrDefault(c => c.Technology == tech.Id);
            var animal = primitive.Biosphere?.Animals.FirstOrDefault(a => a.Technology == tech.Id);
            var annotation = annotations?.GetValueOrDefault(tech.Id);
            var conditions = new List<string>();
            conditions.Add(tech.Baseline ? "Стартовое знание у всех поселений; материальное внедрение учитывается отдельно."
                : $"Практика «{tech.Practice}»: {N(tech.PracticeHours)} человеко-часов. Предпосылки должны быть известны до начала дня.");
            if (tech.Prerequisites.Length > 0)
                conditions.Add("Необходимые знания: " + string.Join(", ", tech.Prerequisites.Select(id => primitive.Technologies.First(t => t.Id == id).Name)) + ".");
            var effects = annotation?.Effects.ToList() ?? [];
            if (crop is not null)
            {
                conditions.Add($"Растение найдено; посадочный материал собран. Полная площадь требует {N(crop.SeedTonnes)} т семян, при нехватке — опытная посадка.");
                conditions.Add($"Ареал: {N(crop.Habitat.MinTemperature)}…{N(crop.Habitat.MaxTemperature)} °C, влажность {N(crop.Habitat.MinMoisture * 100)}…{N(crop.Habitat.MaxMoisture * 100)}%. Рост зависит от текущей погоды.");
                conditions.Add($"Созревание: {N(crop.DegreeDays)} градусо-дней выше {N(crop.BaseTemperature)} °C; многолетнее созревание: {N(crop.MatureYears)} лет.");
                effects.Add($"Открывает посадки «{crop.Name}». Базовый урожай {N(crop.YieldTonnes)} т; доля на семена {N(crop.SeedShare * 100)}%. Урожай не возникает без труда, подходящего сезона и роста.");
                effects.Add("Внедрение определяется площадью реальных посадок; возможность — запасом семян. Собранный урожай учитывается для открытия севооборота.");
            }
            if (animal is not null)
            {
                conditions.Add($"Вид обнаружен; нужен живой отлов ({N(animal.CaptureHours)} ч), корм, вода и уход. Для размножения нужны взрослые самец и самка.");
                conditions.Add($"Ареал: {N(animal.Habitat.MinTemperature)}…{N(animal.Habitat.MaxTemperature)} °C, влажность {N(animal.Habitat.MinMoisture * 100)}…{N(animal.Habitat.MaxMoisture * 100)}%.");
                effects.Add($"Открывает содержание «{animal.Name}». На животное в день: корм {N(animal.FeedPerDay)} т, вода {N(animal.WaterPerDay)} т, уход {N(animal.CareHoursPerDay)} ч.");
                effects.Add($"Созревание {animal.MaturityDays} дней; период размножения {animal.GestationDays} дней. Угодья получают навоз; внедрение определяется фактическим поголовьем.");
            }
            if (tech.Id == "crop_rotation")
            {
                conditions.Add($"Получены урожаи не менее {primitive.Biosphere?.RotationCropCount ?? 3} разных культур (не просто открыты знания).");
                effects.Add("При выборе посева предпочтительна смена семейства. Посадка бобовых после другого семейства восстанавливает качество почвы на 0,025 × долю засеянной площади.");
            }
            if (effects.Count == 0) effects.Add("Отдельный эффект в описании пока не размечен. Это не означает автоматического бонуса к производству.");
            nodes.Add(new("primitive:" + tech.Id, tech.Id, tech.Name, tech.Domain, "primitive",
                crop is not null || animal is not null || tech.Id == "crop_rotation" ? "content/worlds/biosphere.json" : "content/worlds/primordial-rules.json",
                annotation?.Description ?? (crop is not null ? "Видовое знание выращивания, отделённое от семян и фактических посадок."
                    : animal is not null ? "Видовое знание содержания животных, отделённое от живого поголовья." : "Технология начальной эпохи."),
                conditions.ToArray(), effects.ToArray(), crop?.Symbol ?? animal?.Symbol ?? annotation?.Symbol));
            edges.AddRange(tech.Prerequisites.Select(p => new TechnologyEditorEdge($"primitive:{p}>{tech.Id}:required", "primitive:" + p, "primitive:" + tech.Id, "required")));
        }
        // The later catalogue has overlapping IDs but different semantics: never merge by name/ID.
        foreach (var tech in content.Technologies.Technologies)
        {
            var recipes = content.Recipes.Recipes.Where(r => r.RequiredTechnologyIds.Contains(tech.Id)).ToArray();
            nodes.Add(new("catalog:" + tech.Id, tech.Id, tech.Name, tech.Domain, "catalog", "content/technologies.json",
                "Общий каталог прежнего экономического контура. Не считается внедрённой механикой начальной эпохи.",
                [$"Сложность: {N(tech.Complexity)}; коэффициент распространения: {N(tech.Diffusion)}. Связи показаны по каталогу, без переноса в начальный сценарий."],
                recipes.Length > 0 ? recipes.Select(r => "Условие рецепта: " + r.Name + ".").ToArray() : ["Отдельные производственные эффекты в каталоге не указаны."],
                tech.Id == "water_mill" ? "mill" : null));
        }
        edges.AddRange(content.Technologies.Relations.Select(e => new TechnologyEditorEdge($"catalog:{e.From}>{e.To}:{e.Type}", "catalog:" + e.From, "catalog:" + e.To, e.Type)));
        return new(nodes.ToArray(), edges.ToArray());
    }
}
