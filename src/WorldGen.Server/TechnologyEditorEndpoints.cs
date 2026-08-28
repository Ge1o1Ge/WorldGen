using System.Text.Json;
using WorldGen.Content;
using WorldGen.Core.Content;
using WorldGen.Core.Simulation;

namespace WorldGen.Server;

public static class TechnologyEditorEndpoints
{
    public static async Task MapTechnologyEditorAsync(this WebApplication app, string root, ContentCatalog content, PrimitiveWorldRules? primitive)
    {
        var annotations = JsonSerializer.Deserialize<Dictionary<string, TechnologyAnnotation>>(
            await File.ReadAllTextAsync(Path.Combine(root, "content/editor/technology-annotations.json")), TechnologyEditorStore.JsonOptions);
        var catalogue = TechnologyEditorCatalog.Build(content, primitive, annotations);
        var store = new TechnologyEditorStore(Path.Combine(root, app.Configuration["technology-workspace"] ?? "content/editor/technology-workspace.json"), catalogue);
        app.MapGet("/api/technology-editor", async () => Results.Ok(new
        {
            catalog = store.Catalog, catalogVersion = store.CatalogVersion, workspace = await store.ReadAsync()
        }));
        app.MapGet("/api/technology-editor/pending", async () => Results.Ok(new
        {
            catalogVersion = store.CatalogVersion, pending = TechnologyEditorStore.Pending(await store.ReadAsync())
        }));
        app.MapGet("/api/technology-editor/export", async () => Results.Text(JsonSerializer.Serialize(new
        {
            catalog = store.Catalog, catalogVersion = store.CatalogVersion, workspace = await store.ReadAsync()
        }, TechnologyEditorStore.JsonOptions), "application/json; charset=utf-8"));
        app.MapPost("/api/technology-editor/commands", async (HttpContext context, EditorCommand command) =>
        {
            // This is a local, same-origin authoring tool, not a public unauthenticated editing service.
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme)) return Results.StatusCode(403);
            if (context.Request.Headers["X-WorldGen-Editor"] != "1") return Results.StatusCode(403);
            try { return Results.Ok(await store.ApplyAsync(command)); }
            catch (EditorConflictException e) { return Results.Conflict(new { error = e.Message }); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (InvalidDataException e) { return Results.Problem(e.Message, statusCode: 503); }
            catch (IOException) { return Results.Problem("Не удалось сохранить файл. Предыдущая версия остаётся на диске; проверьте права и свободное место.", statusCode: 503); }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2 * 1024 * 1024));
    }
}
