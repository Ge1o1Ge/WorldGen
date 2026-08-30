using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorldGen.Core.Content;
using WorldGen.Core.Simulation;

namespace WorldGen.Content;

public sealed record TechnologyEditorNode(string Id, string TechnologyId, string Title, string Domain,
    string Layer, string Source, string Description, string[] Conditions, string[] Effects, string? Symbol, string Kind = "technology");
public sealed record TechnologyEditorEdge(string Id, string From, string To, string Type);
public sealed record TechnologyAnnotation(string Description, string[] Effects, string? Symbol, string? EditorId = null);
public sealed record TechnologyEditorCatalog(TechnologyEditorNode[] Nodes, TechnologyEditorEdge[] Edges)
{
    public string Version => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, HashOptions))));
    private static readonly JsonSerializerOptions HashOptions = new() { IgnoreReadOnlyProperties = true };

    public static TechnologyEditorCatalog Build(ContentCatalog content, PrimitiveWorldRules? primitive,
        IReadOnlyDictionary<string, TechnologyAnnotation>? annotations = null)
    {
        var nodes = new List<TechnologyEditorNode>();
        var edges = new List<TechnologyEditorEdge>();
        var primitiveIds = (primitive?.Technologies ?? []).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var resourceNames = content.Resources.Resources.Concat(primitive?.Resources ?? [])
            .GroupBy(resource => resource.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        string NodeId(string technologyId) => annotations?.GetValueOrDefault(technologyId)?.EditorId
            ?? (primitiveIds.Contains(technologyId) ? "primitive:" + technologyId : "catalog:" + technologyId);
        static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        foreach (var tech in primitive?.Technologies ?? [])
        {
            var crop = primitive!.Biosphere?.Crops.FirstOrDefault(c => c.Technology == tech.Id);
            var animal = primitive.Biosphere?.Animals.FirstOrDefault(a => a.Technology == tech.Id);
            var processes = primitive.Processes.Where(process => process.Technology == tech.Id).ToArray();
            var animalProducts = primitive.Biosphere?.Animals.SelectMany(owner => owner.ProductRules.Select(product => (Owner: owner, Product: product)))
                .Where(pair => pair.Product.Technology == tech.Id).ToArray() ?? [];
            var annotation = annotations?.GetValueOrDefault(tech.Id);
            var conditions = new List<string>();
            conditions.Add(tech.Baseline ? "Стартовое знание у всех поселений; материальное внедрение учитывается отдельно."
                : $"Практика «{tech.Practice}»: {N(tech.PracticeHours)} человеко-часов. Предпосылки должны быть известны до начала дня.");
            if (tech.Prerequisites.Length > 0)
                conditions.Add("Необходимые знания: " + string.Join(", ", tech.Prerequisites.Select(id => primitive.Technologies.First(t => t.Id == id).Name)) + ".");
            if (tech.AlternativePrerequisites.Length > 0)
                conditions.Add("Нужен хотя бы один альтернативный путь: " + string.Join(" или ", tech.AlternativePrerequisites.Select(id => primitive.Technologies.First(t => t.Id == id).Name)) + ".");
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
            foreach (var pair in animalProducts)
            {
                conditions.Add($"Для продукта «{pair.Product.Name}» нужны самки вида «{pair.Owner.Name}», уход и {N(pair.Product.LaborHoursPerUnit)} ч труда на единицу." +
                    (pair.Product.LactationDays > 0 ? $" Получение возможно {pair.Product.LactationDays} дней после приплода." : ""));
                effects.Add($"Реальная партия «{pair.Product.Name}»: {N(pair.Product.PerFemalePerDay)} {pair.Product.Unit} на самку в день; базовая порча {N(pair.Product.DecayPerDay * 100)}% в день.");
            }
            foreach (var process in processes)
            {
                string Amounts(IEnumerable<KeyValuePair<string, double>> amounts) => string.Join(", ", amounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{resourceNames.GetValueOrDefault(pair.Key, pair.Key)} {N(pair.Value)}"));
                conditions.Add($"Процесс «{process.Name}»: входы {Amounts(process.Inputs)}; оснащение {Amounts(process.RequiredStocks)}; труд {N(process.LaborHoursPerBatch)} ч на партию.");
                if (process.BuildingRequirements.Length > 0)
                    conditions.Add("Для процесса нужна хотя бы одна действующая установка: " + string.Join(" или ", process.BuildingRequirements) + ".");
                effects.Add($"Выход процесса: {Amounts(process.Outputs)}. Целевой запас {N(process.TargetOutputPerPerson)} {resourceNames.GetValueOrDefault(process.TargetResource, process.TargetResource)} на жителя; фактические партии ограничены трудом, входами и оснащением.");
                if (process.LaborMultipliers.Count > 0)
                    effects.Add("Труд на той же партии зависит от установки: " + string.Join(", ", process.LaborMultipliers.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} ×{N(pair.Value)}")) + ".");
            }
            if (tech.Id == "crop_rotation")
            {
                conditions.Add($"Получены урожаи не менее {primitive.Biosphere?.RotationCropCount ?? 3} разных культур (не просто открыты знания).");
                effects.Add("При выборе посева предпочтительна смена семейства. Посадка бобовых после другого семейства восстанавливает качество почвы на 0,025 × долю засеянной площади.");
            }
            if (effects.Count == 0) effects.Add("Отдельный эффект в описании пока не размечен. Это не означает автоматического бонуса к производству.");
            nodes.Add(new(NodeId(tech.Id), tech.Id, tech.Name, tech.Domain, "primitive",
                crop is not null || animal is not null || tech.Id == "crop_rotation" ? "content/worlds/biosphere.json" : "content/worlds/primordial-rules.json",
                annotation?.Description ?? (crop is not null ? "Видовое знание выращивания, отделённое от семян и фактических посадок."
                    : animal is not null ? "Видовое знание содержания животных, отделённое от живого поголовья." : "Технология начальной эпохи."),
                conditions.ToArray(), effects.ToArray(), crop?.Symbol ?? animal?.Symbol ?? annotation?.Symbol));
            edges.AddRange(tech.Prerequisites.Select(p => new TechnologyEditorEdge(
                $"{NodeId(p)}>{NodeId(tech.Id)}:required", NodeId(p), NodeId(tech.Id), "required")));
            if (tech.AlternativePrerequisites.Length > 0)
            {
                var logicId = "logic:any:" + tech.Id;
                nodes.Add(new(logicId, "any:" + tech.Id, "ИЛИ", tech.Domain, "primitive", "anyPrerequisites",
                    "Достаточно одного из входящих альтернативных знаний.",
                    tech.AlternativePrerequisites.Select(id => primitive.Technologies.First(t => t.Id == id).Name).ToArray(),
                    [$"Разрешает обязательное условие для «{tech.Name}» при наличии любого одного входа."], null, "logic"));
                edges.AddRange(tech.AlternativePrerequisites.Select(p => new TechnologyEditorEdge(
                    $"{NodeId(p)}>{logicId}:alternative", NodeId(p), logicId, "alternative")));
                edges.Add(new($"{logicId}>{NodeId(tech.Id)}:required", logicId, NodeId(tech.Id), "required"));
            }
        }
        edges.AddRange((primitive?.Relations ?? []).Select(relation => new TechnologyEditorEdge(
            $"{NodeId(relation.From)}>{NodeId(relation.To)}:{relation.Type}", NodeId(relation.From), NodeId(relation.To), relation.Type)));
        // There is one organic knowledge graph. Primitive definitions are the
        // authoritative version of overlapping IDs; the remaining catalogue
        // definitions stay connected in the same layer instead of forming an era.
        foreach (var tech in content.Technologies.Technologies.Where(t => !primitiveIds.Contains(t.Id)))
        {
            var recipes = content.Recipes.Recipes.Where(r => r.RequiredTechnologyIds.Contains(tech.Id)).ToArray();
            nodes.Add(new("catalog:" + tech.Id, tech.Id, tech.Name, tech.Domain, "primitive", "content/technologies.json",
                "Технология единого каталога знаний. Её положение определяется связями и условиями, а не назначенной эпохой.",
                [$"Сложность освоения: {N(tech.Complexity)}; коэффициент распространения: {N(tech.Diffusion)}."],
                recipes.Length > 0 ? recipes.Select(r => "Условие рецепта: " + r.Name + ".").ToArray() : ["Отдельные производственные эффекты в каталоге не указаны."],
                tech.Id == "water_mill" ? "mill" : null));
        }
        edges.AddRange(content.Technologies.Relations.Select(e => new TechnologyEditorEdge($"catalog:{e.From}>{e.To}:{e.Type}", NodeId(e.From), NodeId(e.To), e.Type)));
        var uniqueEdges = edges.GroupBy(e => (e.From, e.To, e.Type)).Select(group => group.First()).ToArray();
        if (nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw new InvalidDataException("Редакторские ID технологий должны быть уникальными");
        return new(nodes.ToArray(), uniqueEdges);
    }
}
