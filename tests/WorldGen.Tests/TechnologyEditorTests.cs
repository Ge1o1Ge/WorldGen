using System.Text.Json;
using WorldGen.Content;

namespace WorldGen.Tests;

public sealed class TechnologyEditorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "worldgen-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TechnologyEditorCatalog catalog = new([
        new("primitive:a", "a", "Источник", "food", "primitive", "source.json", "", [], [], null),
        new("primitive:b", "b", "Результат", "food", "primitive", "source.json", "", [], [], null)
    ], [new("ab", "primitive:a", "primitive:b", "required")]);
    private string FilePath => Path.Combine(directory, "workspace.json");
    private TechnologyEditorStore Store => new(FilePath, catalog);
    private EditorCommand Command(string action, long revision = 0) => new() { Action = action, Revision = revision, CatalogVersion = catalog.Version };
    private static EditorDraft Draft(string id = "draft:test") => new(id, "Тестовая мысль", "Описание", "Условие", "Эффект", "craft", null);

    [Fact]
    public async Task PersistsDraftWithoutModifyingCatalogAndKeepsBackup()
    {
        var store = Store; var version = catalog.Version;
        var state = await store.ApplyAsync(Command("create-node") with { Node = Draft() with { Status = "adapted", TargetId = "primitive:a" } });
        Assert.Equal("manual", state.Nodes.Single().Status); Assert.Null(state.Nodes.Single().TargetId);
        state = await store.ApplyAsync(Command("add-comment", 1) with { Id = "note", NodeId = "draft:test", Text = "Проверить семена <script>не HTML</script>" });
        var reread = await Store.ReadAsync();
        Assert.Equal(2, reread.Revision); Assert.Contains("<script>", reread.Comments.Single().Text);
        Assert.Equal(version, catalog.Version); Assert.Equal(2, catalog.Nodes.Length);
        var backup = JsonSerializer.Deserialize<TechnologyWorkspace>(await File.ReadAllTextAsync(FilePath + ".bak"), TechnologyEditorStore.JsonOptions)!;
        Assert.Equal(1, backup.Revision); Assert.Empty(backup.Comments);
    }
    [Fact]
    public async Task LayoutSurvivesReopeningDraftEditsCommentsAndOtherNodeMoves()
    {
        var store = Store;
        await store.ApplyAsync(Command("create-node") with { Node = Draft() });
        var manualPosition = new EditorPosition(-301.75, 458.125);
        var sourcePosition = new EditorPosition(2300, -80);
        await store.ApplyAsync(Command("move-nodes", 1) with { Positions = new() { ["draft:test"] = manualPosition, ["primitive:a"] = sourcePosition } });
        store = Store; // Simulates reopening the server: no in-memory position cache.
        await store.ApplyAsync(Command("edit-node", 2) with { Node = Draft() with { Title = "Новое имя" } });
        await store.ApplyAsync(Command("add-comment", 3) with { Id = "note", NodeId = "draft:test", Text = "Сохранить место ноды" });
        await store.ApplyAsync(Command("create-node", 4) with { Node = Draft("draft:new") });
        await store.ApplyAsync(Command("move-nodes", 5) with { Positions = new() { ["draft:new"] = new(10, 20) } });
        var state = await Store.ReadAsync();
        Assert.Equal(manualPosition, state.Positions["draft:test"]);
        Assert.Equal(sourcePosition, state.Positions["primitive:a"]);
        Assert.Equal("manual", state.Nodes.Single(n => n.Id == "draft:test").Status);
        Assert.Equal(3, state.Positions.Count);
    }
    [Fact]
    public async Task StaleTabAndChangedCatalogCannotOverwrite()
    {
        var store = Store;
        await store.ApplyAsync(Command("create-node") with { Node = Draft() });
        await Assert.ThrowsAsync<EditorConflictException>(() => store.ApplyAsync(Command("create-node") with { Node = Draft("draft:other") }));
        await Assert.ThrowsAsync<EditorConflictException>(() => store.ApplyAsync(Command("add-comment", 1) with { CatalogVersion = "old", Id = "note", NodeId = "primitive:a", Text = "text" }));
        Assert.Single((await Store.ReadAsync()).Nodes);
    }
    [Fact]
    public async Task ConcurrentCommandsWithOneRevisionOnlyCommitOnce()
    {
        var store = Store;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await store.ApplyAsync(Command("create-node") with { Node = Draft("draft:" + i) }); return true; }
            catch (EditorConflictException) { return false; }
        }));
        Assert.Single(results, r => r); Assert.Equal(1, (await store.ReadAsync()).Revision);
    }
    [Fact]
    public async Task InvalidOperationsAreAtomic()
    {
        var store = Store;
        await store.ApplyAsync(Command("create-node") with { Node = Draft() });
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("move-nodes", 1) with { Positions = new() { ["draft:test"] = new(double.NaN, 10) } }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("add-edge", 1) with { Edge = new("edge", "draft:test", "absent", "required") }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("add-edge", 1) with { Edge = new("edge", "draft:test", "draft:test", "required") }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("add-edge", 1) with { Edge = new("edge", "primitive:a", "primitive:b", "required") }));
        var state = await store.ReadAsync(); Assert.Equal(1, state.Revision); Assert.Empty(state.Positions); Assert.Empty(state.Edges);
    }
    [Fact]
    public async Task ReviewArchivesExactCommentsAndPreservesUnreviewedOnes()
    {
        var store = Store;
        await store.ApplyAsync(Command("create-node") with { Node = Draft() });
        await store.ApplyAsync(Command("add-comment", 1) with { Id = "note", NodeId = "draft:test", Text = "Исходная мысль" });
        await store.ApplyAsync(Command("add-comment", 2) with { Id = "later", NodeId = "draft:test", Text = "Ещё обсудить" });
        var review = new EditorReview("review", ["draft:test"], ["note"], [], "primitive:a", "Интегрировано", ["source.json", "tests.cs"], default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("review", 3) with { Review = review with { CommentIds = ["absent"] } }));
        Assert.Equal("manual", (await store.ReadAsync()).Nodes.Single().Status);
        var state = await store.ApplyAsync(Command("review", 3) with { Review = review });
        Assert.Equal("adapted", state.Nodes.Single().Status); Assert.Equal("primitive:a", state.Nodes.Single().TargetId);
        Assert.Equal("adapted", state.Comments[0].Status); Assert.Equal("Исходная мысль", state.Comments[0].Text); Assert.Equal("manual", state.Comments[1].Status);
        Assert.Single(state.Journal); Assert.NotEqual(default, state.Journal[0].CreatedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("edit-node", 4) with { Node = Draft() }));
        var pending = JsonSerializer.Serialize(TechnologyEditorStore.Pending(state), TechnologyEditorStore.JsonOptions);
        Assert.Contains("Ещё обсудить", pending); Assert.DoesNotContain("Исходная мысль", pending);
    }
    [Fact]
    public async Task ReviewRequiresTargetEvidenceAndMatchingCommentOwner()
    {
        var store = Store;
        await store.ApplyAsync(Command("add-comment") with { Id = "note", NodeId = "primitive:a", Text = "Исправить" });
        var review = new EditorReview("review", [], ["note"], [], "primitive:a", "Готово", ["source.json"], default);
        foreach (var bad in new[] { review with { TargetId = "absent" }, review with { References = [] }, review with { TargetId = "primitive:b" }, review with { Summary = " " } })
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("review", 1) with { Review = bad }));
        Assert.Empty((await store.ReadAsync()).Journal);
    }
    [Fact]
    public async Task NecessaryLinkMustExistInCatalogueBeforeReviewAndWithdrawIsRecoverable()
    {
        var store = Store;
        await store.ApplyAsync(Command("add-edge") with { Edge = new("edge", "primitive:b", "primitive:a", "required") });
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("review", 1) with {
            Review = new("review", [], [], ["edge"], "primitive:a", "Готово", ["source.json"], default)
        }));
        var state = await store.ApplyAsync(Command("withdraw-edge", 1) with { Id = "edge" });
        Assert.Equal("withdrawn", state.Edges.Single().Status);
        Assert.Empty(JsonSerializer.SerializeToNode(TechnologyEditorStore.Pending(state))!["edges"]!.AsArray());
    }
    [Fact]
    public async Task PartialReviewKeepsRemainingWorkManualAndEveryImplementationStageInJournal()
    {
        var store = Store;
        const string original = "Добавить печь, совместное приготовление пищи и общественный склад";
        await store.ApplyAsync(Command("add-comment") with { Id = "complex", NodeId = "primitive:a", Text = original });
        var first = new EditorReview("stage1", [], [], [], "primitive:a", "Первый этап", ["source.json"], default,
            [new("complex", "Добавлена печь", "Совместная готовка и общественный склад")]);
        var partial = await store.ApplyAsync(Command("review", 1) with { Review = first });
        Assert.Equal("manual", partial.Comments.Single().Status); Assert.Equal(original, partial.Comments.Single().Text);
        Assert.Equal("Совместная готовка и общественный склад", partial.Comments.Single().RemainingText);
        Assert.Contains("Совместная готовка", JsonSerializer.Serialize(TechnologyEditorStore.Pending(partial), TechnologyEditorStore.JsonOptions));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(Command("review", 2) with { Review = first with { Id = "bad", CommentProgress = [new("complex", "", "Остаток")] } }));
        var second = await store.ApplyAsync(Command("review", 2) with { Review = first with { Id = "stage2", CommentProgress = [new("complex", "Добавлена совместная готовка", "Общественный склад")] } });
        Assert.Equal(2, second.Journal.Count); Assert.Equal("Добавлена печь", second.Journal[0].CommentProgress![0].Implemented);
        var final = await store.ApplyAsync(Command("review", 3) with { Review = first with { Id = "stage3", CommentProgress = [new("complex", "Добавлен общественный склад", "")] } });
        Assert.Equal("adapted", final.Comments.Single().Status); Assert.Equal(original, final.Comments.Single().Text); Assert.Equal(3, final.Journal.Count);
    }
    [Fact]
    public async Task UnknownOrBrokenFileIsNeverResetSilently()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "{\"schemaVersion\":99}");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.ReadAsync());
        await File.WriteAllTextAsync(FilePath, "{ broken");
        await Assert.ThrowsAsync<JsonException>(() => Store.ReadAsync());
        Assert.Equal("{ broken", await File.ReadAllTextAsync(FilePath));
    }
    [Fact]
    public async Task CatalogContainsActualSpeciesRequirementsAndSeparateLegacyLayer()
    {
        var content = await ContentLoader.LoadAsync(); var rules = await SettlementRulesLoader.LoadAsync(scenario: "primordial");
        var graph = TechnologyEditorCatalog.Build(content, rules.Primitive);
        Assert.Equal(65, graph.Nodes.Length); Assert.Equal(65, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Contains(graph.Nodes, n => n.Id == "primitive:woodworking"); Assert.Contains(graph.Nodes, n => n.Id == "catalog:woodworking");
        var wheat = graph.Nodes.Single(n => n.Id == "primitive:grow_wheat");
        Assert.Equal("grain", wheat.Symbol); Assert.Contains(wheat.Conditions, c => c.Contains("семян"));
        Assert.Contains(graph.Edges, e => e.From == "primitive:gardening" && e.To == wheat.Id && e.Type == "required");
        Assert.Contains(graph.Nodes.Single(n => n.Id == "primitive:crop_rotation").Conditions, c => c.Contains("не менее 3"));
        Assert.Equal(64, graph.Version.Length); Assert.Equal(graph.Version, TechnologyEditorCatalog.Build(content, rules.Primitive).Version);
        Assert.All(graph.Edges, e => { Assert.Contains(graph.Nodes, n => n.Id == e.From); Assert.Contains(graph.Nodes, n => n.Id == e.To); });
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
