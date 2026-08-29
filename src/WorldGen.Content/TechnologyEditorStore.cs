using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldGen.Content;

public sealed record EditorPosition(double X, double Y);
public sealed record EditorDraft(string Id, string Title, string Description, string Conditions, string Effects,
    string Domain, string? Symbol, string Status = "manual", string? TargetId = null);
public sealed record EditorLink(string Id, string From, string To, string Type, string Status = "manual");
public sealed record EditorComment(string Id, string NodeId, string Text, DateTimeOffset CreatedAt, string Status = "manual", string? RemainingText = null);
public sealed record EditorCommentProgress(string CommentId, string Implemented, string Remaining);
public sealed record EditorEdgeAdaptation(string EdgeId, string ImplementedType);
public sealed record EditorReview(string Id, string[] NodeIds, string[] CommentIds, string[] EdgeIds,
    string TargetId, string Summary, string[] References, DateTimeOffset CreatedAt, EditorCommentProgress[]? CommentProgress = null,
    EditorEdgeAdaptation[]? EdgeAdaptations = null);
public sealed record TechnologyWorkspace
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; set; }
    public List<EditorDraft> Nodes { get; init; } = [];
    public List<EditorLink> Edges { get; init; } = [];
    public List<EditorComment> Comments { get; init; } = [];
    public List<EditorReview> Journal { get; init; } = [];
    public Dictionary<string, EditorPosition> Positions { get; init; } = new(StringComparer.Ordinal);
}
public sealed record EditorCommand
{
    public long Revision { get; init; }
    public required string CatalogVersion { get; init; }
    public required string Action { get; init; }
    public string? Id { get; init; }
    public string? NodeId { get; init; }
    public string? Text { get; init; }
    public EditorDraft? Node { get; init; }
    public EditorLink? Edge { get; init; }
    public Dictionary<string, EditorPosition>? Positions { get; init; }
    public EditorReview? Review { get; init; }
}
public sealed class EditorConflictException() : Exception("Сеть уже изменилась в другой вкладке или обновился каталог. Обновите сеть; введённый текст не отправлен.");

/// <summary>Drafts are a review workspace, never an executable simulation catalogue.</summary>
public sealed class TechnologyEditorStore(string path, TechnologyEditorCatalog catalog)
{
    public static readonly string[] LinkTypes = ["required", "enables", "supports", "helps", "industrial", "alternative"];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string catalogVersion = catalog.Version;
    public TechnologyEditorCatalog Catalog => catalog;
    public string CatalogVersion => catalogVersion;

    public async Task<TechnologyWorkspace> ReadAsync()
    {
        await gate.WaitAsync();
        try { return await LoadAsync(); }
        finally { gate.Release(); }
    }

    private async Task<TechnologyWorkspace> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        var state = JsonSerializer.Deserialize<TechnologyWorkspace>(await File.ReadAllTextAsync(path), JsonOptions)
            ?? throw new InvalidDataException("Пустой файл редактора");
        Validate(state);
        return state;
    }

    public static object Pending(TechnologyWorkspace state) => new
    {
        state.Revision,
        nodes = state.Nodes.Where(n => n.Status == "manual").ToArray(),
        edges = state.Edges.Where(e => e.Status == "manual").ToArray(),
        comments = state.Comments.Where(c => c.Status == "manual").ToArray()
    };

    public async Task<TechnologyWorkspace> ApplyAsync(EditorCommand command)
    {
        await gate.WaitAsync();
        try
        {
            // Reload disk on every transaction: assistant edits and UI writes share a revision protocol.
            var state = await LoadAsync();
            if (command.Revision != state.Revision || command.CatalogVersion != catalogVersion) throw new EditorConflictException();
            bool Exists(string id) => catalog.Nodes.Any(n => n.Id == id) || state.Nodes.Any(n => n.Id == id);
            switch (command.Action)
            {
                case "create-node":
                case "edit-node":
                    var node = command.Node ?? throw new ArgumentException("Нет ноды");
                    RequireText(node.Id, 120); RequireText(node.Title, 160);
                    if (!node.Id.StartsWith("draft:", StringComparison.Ordinal)) throw new ArgumentException("ID черновика должен начинаться с draft:");
                    var previous = state.Nodes.FindIndex(n => n.Id == node.Id);
                    if (command.Action == "create-node" && Exists(node.Id) || command.Action == "edit-node" && previous < 0)
                        throw new ArgumentException("Нода уже существует или не найдена");
                    if (previous >= 0 && state.Nodes[previous].Status != "manual") throw new ArgumentException("Адаптированная нода неизменяема; добавьте комментарий");
                    node = node with { Status = "manual", TargetId = null };
                    if (previous >= 0) state.Nodes[previous] = node; else state.Nodes.Add(node);
                    break;
                case "add-comment":
                    if (command.NodeId is null || !Exists(command.NodeId)) throw new ArgumentException("Нода комментария не найдена");
                    RequireText(command.Id, 120); RequireText(command.Text, 16000);
                    if (state.Comments.Any(c => c.Id == command.Id)) throw new ArgumentException("Комментарий с этим ID уже существует");
                    state.Comments.Add(new(command.Id!, command.NodeId, command.Text!.Trim(), DateTimeOffset.UtcNow));
                    break;
                case "add-edge":
                    var edge = command.Edge ?? throw new ArgumentException("Нет связи");
                    RequireText(edge.Id, 120);
                    if (!Exists(edge.From) || !Exists(edge.To) || edge.From == edge.To || !LinkTypes.Contains(edge.Type))
                        throw new ArgumentException("Проверьте концы и тип связи; петля в ту же ноду не допускается");
                    if (state.Edges.Any(e => e.Id == edge.Id) || state.Edges.Any(e => e.Status != "withdrawn" && e.From == edge.From && e.To == edge.To && e.Type == edge.Type) ||
                        catalog.Edges.Any(e => e.From == edge.From && e.To == edge.To && e.Type == edge.Type))
                        throw new ArgumentException("Такая связь уже есть");
                    state.Edges.Add(edge with { Status = "manual" });
                    break;
                case "withdraw-edge":
                    var index = state.Edges.FindIndex(e => e.Id == command.Id && e.Status == "manual");
                    if (index < 0) throw new ArgumentException("Можно отозвать только предложенную связь");
                    state.Edges[index] = state.Edges[index] with { Status = "withdrawn" };
                    break;
                case "move-nodes":
                    foreach (var (id, position) in command.Positions ?? throw new ArgumentException("Нет координат"))
                    {
                        if (!Exists(id)) throw new ArgumentException("Неизвестная нода в раскладке");
                        state.Positions[id] = position;
                    }
                    break;
                case "review":
                    var review = command.Review ?? throw new ArgumentException("Нет результата адаптации");
                    RequireText(review.Id, 120); RequireText(review.Summary, 16000);
                    if (!catalog.Nodes.Any(n => n.Id == review.TargetId)) throw new ArgumentException("Адаптация требует существующей ноды каталога");
                    if (review.References is null || review.References.Length == 0) throw new ArgumentException("Укажите файлы/тесты, подтверждающие адаптацию");
                    foreach (var reference in review.References) RequireText(reference, 500);
                    if (state.Journal.Any(r => r.Id == review.Id) || review.NodeIds is null || review.CommentIds is null || review.EdgeIds is null ||
                        review.NodeIds.Length + review.CommentIds.Length + review.EdgeIds.Length + (review.CommentProgress?.Length ?? 0) +
                        (review.EdgeAdaptations?.Length ?? 0) == 0) throw new ArgumentException("Пустой или повторный результат адаптации");
                    foreach (var id in review.NodeIds)
                    {
                        var i = state.Nodes.FindIndex(n => n.Id == id && n.Status == "manual");
                        if (i < 0) throw new ArgumentException("Нода для адаптации не найдена или уже обработана");
                        // A new canonical node inherits the user's deliberate graph position.
                        // Never overwrite a canonical position that the user already placed.
                        if (state.Positions.TryGetValue(id, out var position) && !state.Positions.ContainsKey(review.TargetId))
                            state.Positions[review.TargetId] = position;
                        state.Nodes[i] = state.Nodes[i] with { Status = "adapted", TargetId = review.TargetId };
                    }
                    foreach (var id in review.CommentIds)
                    {
                        var i = state.Comments.FindIndex(c => c.Id == id && c.Status == "manual");
                        if (i < 0) throw new ArgumentException("Комментарий для адаптации не найден или уже обработан");
                        var owner = state.Comments[i].NodeId;
                        var target = state.Nodes.FirstOrDefault(n => n.Id == owner)?.TargetId ?? owner;
                        if (target != review.TargetId) throw new ArgumentException("Комментарий относится к другой ноде");
                        state.Comments[i] = state.Comments[i] with { Status = "adapted", RemainingText = "" };
                    }
                    var progressedIds = new HashSet<string>(review.CommentIds);
                    foreach (var progress in review.CommentProgress ?? [])
                    {
                        if (!progressedIds.Add(progress.CommentId)) throw new ArgumentException("Комментарий указан в адаптации дважды");
                        RequireText(progress.Implemented, 16000);
                        if (progress.Remaining is null || progress.Remaining.Length > 16000) throw new ArgumentException("Нужно явно указать остаток работы; пустая строка означает завершение");
                        var i = state.Comments.FindIndex(c => c.Id == progress.CommentId && c.Status == "manual");
                        if (i < 0) throw new ArgumentException("Комментарий для частичной адаптации не найден или уже завершён");
                        var original = state.Comments[i];
                        var target = state.Nodes.FirstOrDefault(n => n.Id == original.NodeId)?.TargetId ?? original.NodeId;
                        if (target != review.TargetId) throw new ArgumentException("Часть комментария относится к другой ноде");
                        var remaining = progress.Remaining.Trim();
                        state.Comments[i] = original with { Status = remaining.Length > 0 ? "manual" : "adapted", RemainingText = remaining };
                    }
                    foreach (var id in review.EdgeIds)
                    {
                        var i = state.Edges.FindIndex(e => e.Id == id && e.Status == "manual");
                        if (i < 0) throw new ArgumentException("Связь для адаптации не найдена или уже обработана");
                        var e = state.Edges[i];
                        string Resolve(string nodeId) => state.Nodes.FirstOrDefault(n => n.Id == nodeId)?.TargetId ?? nodeId;
                        if (Resolve(e.From).StartsWith("draft:") || Resolve(e.To).StartsWith("draft:"))
                            throw new ArgumentException("Сначала адаптируйте обе ноды связи");
                        // Required links must actually exist in the executable/source catalogue.
                        if (e.Type == "required" && !catalog.Edges.Any(c => c.From == Resolve(e.From) && c.To == Resolve(e.To) && c.Type == "required"))
                            throw new ArgumentException("Необходимая связь ещё не внесена в исходный каталог");
                        state.Edges[i] = e with { Status = "adapted" };
                    }
                    var exactEdgeIds = review.EdgeIds.ToHashSet(StringComparer.Ordinal);
                    foreach (var adaptation in review.EdgeAdaptations ?? [])
                    {
                        if (!exactEdgeIds.Add(adaptation.EdgeId)) throw new ArgumentException("Связь указана в адаптации дважды");
                        var i = state.Edges.FindIndex(e => e.Id == adaptation.EdgeId && e.Status == "manual");
                        if (i < 0) throw new ArgumentException("Связь для нормализации не найдена или уже обработана");
                        var proposedEdge = state.Edges[i];
                        string Resolve(string nodeId) => state.Nodes.FirstOrDefault(n => n.Id == nodeId)?.TargetId ?? nodeId;
                        var from = Resolve(proposedEdge.From); var to = Resolve(proposedEdge.To);
                        var direct = catalog.Edges.Any(source => source.From == from && source.To == to && source.Type == adaptation.ImplementedType);
                        var throughOr = adaptation.ImplementedType == "alternative" && catalog.Edges.Any(first => first.From == from &&
                            first.Type == "alternative" && first.To.StartsWith("logic:any:", StringComparison.Ordinal) &&
                            catalog.Edges.Any(last => last.From == first.To && last.To == to && last.Type == "required"));
                        if (!LinkTypes.Contains(adaptation.ImplementedType) || !direct && !throughOr)
                            throw new ArgumentException("Нормализованный тип связи ещё не внесён в исходный каталог");
                        state.Edges[i] = proposedEdge with { Status = "adapted" };
                    }
                    state.Journal.Add(review with { CreatedAt = DateTimeOffset.UtcNow });
                    break;
                default: throw new ArgumentException("Неизвестная команда редактора");
            }
            state.Revision++;
            Validate(state);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, JsonOptions));
                // Keep the previous successful revision recoverable; publish atomically on the same volume.
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return state;
        }
        finally { gate.Release(); }
    }

    private static void RequireText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"Ожидается текст длиной 1…{max}");
    }
    private static void Validate(TechnologyWorkspace state)
    {
        if (state.SchemaVersion != 1 || state.Revision < 0 || state.Nodes is null || state.Edges is null || state.Comments is null || state.Journal is null || state.Positions is null)
            throw new InvalidDataException("Неизвестная или повреждённая структура файла редактора");
        if (state.Nodes.Count > 5000 || state.Edges.Count > 20000 || state.Comments.Count > 50000) throw new ArgumentException("Превышен лимит рабочего пространства");
        foreach (var n in state.Nodes)
        {
            RequireText(n.Id, 120); RequireText(n.Title, 160); RequireText(n.Domain, 80);
            if (n.Description is null || n.Conditions is null || n.Effects is null || n.Description.Length > 16000 || n.Conditions.Length > 16000 || n.Effects.Length > 16000 || n.Symbol?.Length > 80 ||
                !n.Id.StartsWith("draft:") || n.Status is not ("manual" or "adapted") || n.Status == "adapted" && string.IsNullOrWhiteSpace(n.TargetId))
                throw new ArgumentException("Некорректная нода редактора");
        }
        foreach (var c in state.Comments)
        {
            RequireText(c.Id, 120); RequireText(c.NodeId, 160); RequireText(c.Text, 16000);
            if (c.Status is not ("manual" or "adapted") || c.RemainingText?.Length > 16000 || c.Status == "manual" && c.RemainingText is not null && string.IsNullOrWhiteSpace(c.RemainingText))
                throw new ArgumentException("Некорректный статус или остаток комментария");
        }
        foreach (var e in state.Edges)
            if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To) || e.From == e.To ||
                !LinkTypes.Contains(e.Type) || e.Status is not ("manual" or "adapted" or "withdrawn")) throw new ArgumentException("Некорректная связь");
        if (state.Nodes.Select(n => n.Id).Distinct().Count() != state.Nodes.Count || state.Edges.Select(e => e.Id).Distinct().Count() != state.Edges.Count ||
            state.Comments.Select(c => c.Id).Distinct().Count() != state.Comments.Count) throw new ArgumentException("Повторяющиеся ID в файле редактора");
        if (state.Positions.Values.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1000000 || Math.Abs(p.Y) > 1000000))
            throw new ArgumentException("Недопустимые координаты");
    }
}
