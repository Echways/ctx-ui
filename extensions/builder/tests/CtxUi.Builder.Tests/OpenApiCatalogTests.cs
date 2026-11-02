using System.Text;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Servers;
using CtxUi.Builder.Tools;
using Enfilade;

namespace CtxUi.Builder.Tests;

public sealed class OpenApiCatalogTests : IClassFixture<CatalogHarness>
{
    private readonly CatalogHarness _harness;

    public OpenApiCatalogTests(CatalogHarness harness) => _harness = harness;

    private ServerCatalog Catalog => _harness.Catalog;

    private static string Slug() => "s" + Guid.NewGuid().ToString("N")[..12];

    private static FormTool Tool(string name = "get_forecast") =>
        new(name, "GET", $"/{name}", "Forecast", [new ToolParameter("days", "integer", ParameterPlace.Query, false, null, null)], null);

    private async Task<Guid> ServerAsync() => (await Catalog.CreateAsync(Slug(), "Weather", default)).Id;

    [Fact]
    public async Task Saved_api_document_is_stored_in_the_draft_and_advances_the_revision()
    {
        var id = await ServerAsync();
        var before = (await Catalog.DraftAsync(id, default)).Revision;

        var revision = await Catalog.SaveOpenApiAsync(id, OpenApiDraft.Upsert(string.Empty, Tool()), before, default);

        var draft = await Catalog.DraftAsync(id, default);
        Assert.Equal(before + 1, revision);
        Assert.Equal(revision, draft.Revision);
        Assert.Equal("get_forecast", Assert.Single(OpenApiDraft.Read(draft.OpenApiJson!)).Name);
    }

    [Fact]
    public async Task A_stale_api_save_raises_a_conflict_and_keeps_the_newer_document()
    {
        var id = await ServerAsync();
        var start = (await Catalog.DraftAsync(id, default)).Revision;
        var newer = OpenApiDraft.Upsert(string.Empty, Tool("newer"));
        await Catalog.SaveOpenApiAsync(id, newer, start, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.SaveOpenApiAsync(id, OpenApiDraft.Upsert(string.Empty, Tool("stale")), start, default));

        Assert.Equal(newer, (await Catalog.DraftAsync(id, default)).OpenApiJson);
    }

    [Fact]
    public async Task A_document_that_is_not_json_is_refused_before_saving()
    {
        var id = await ServerAsync();

        var revision = (await Catalog.DraftAsync(id, default)).Revision;

        await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveOpenApiAsync(id, "{ nope", revision, default));

        Assert.Null((await Catalog.DraftAsync(id, default)).OpenApiJson);
    }

    [Fact]
    public async Task Converting_to_code_writes_the_file_and_drops_the_operation_together()
    {
        var id = await ServerAsync();
        var revision = await Catalog.SaveOpenApiAsync(id, OpenApiDraft.Upsert(string.Empty, Tool()), (await Catalog.DraftAsync(id, default)).Revision, default);

        var (path, next) = await Catalog.ConvertToCodeAsync(id, "get_forecast", revision, default);

        var draft = await Catalog.DraftAsync(id, default);
        Assert.Equal("mcp/get_forecast.py", path);
        Assert.Equal(next, draft.Revision);
        Assert.Contains("async def get_forecast", Assert.Single(draft.Files).Content);
        Assert.Empty(OpenApiDraft.Read(draft.OpenApiJson!));
    }

    [Fact]
    public async Task Converting_to_code_over_an_existing_file_changes_nothing()
    {
        var id = await ServerAsync();
        await Catalog.SaveFileAsync(id, "mcp/get_forecast.py", "mine", 0, default);
        var document = OpenApiDraft.Upsert(string.Empty, Tool());
        var revision = await Catalog.SaveOpenApiAsync(id, document, (await Catalog.DraftAsync(id, default)).Revision, default);

        var failure = await Assert.ThrowsAsync<PanelException>(() => Catalog.ConvertToCodeAsync(id, "get_forecast", revision, default));

        var draft = await Catalog.DraftAsync(id, default);
        Assert.Equal(BuilderText.ToolExists, failure.Message);
        Assert.Equal(document, draft.OpenApiJson);
        Assert.Equal(revision, draft.Revision);
        Assert.Equal("mine", Assert.Single(draft.Files).Content);
    }

    [Fact]
    public async Task Converting_with_a_stale_revision_is_a_conflict()
    {
        var id = await ServerAsync();
        var revision = await Catalog.SaveOpenApiAsync(id, OpenApiDraft.Upsert(string.Empty, Tool()), (await Catalog.DraftAsync(id, default)).Revision, default);
        await Catalog.SaveFileAsync(id, "mcp/other.py", "x", 0, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.ConvertToCodeAsync(id, "get_forecast", revision, default));

        Assert.Single(OpenApiDraft.Read((await Catalog.DraftAsync(id, default)).OpenApiJson!));
    }

    [Fact]
    public async Task Imported_operations_are_merged_into_the_draft_and_stale_imports_conflict()
    {
        var id = await ServerAsync();
        var document = """{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"operationId":"a","responses":{"200":{"description":"OK"}}}},"/b":{"get":{"operationId":"b","responses":{"200":{"description":"OK"}}}}}}""";
        var api = await OpenApiImport.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(document)), default);
        var start = (await Catalog.DraftAsync(id, default)).Revision;

        var revision = await Catalog.ImportOperationsAsync(id, api, new HashSet<string> { "a" }, start, default);

        Assert.Equal(["a"], OpenApiDraft.Read((await Catalog.DraftAsync(id, default)).OpenApiJson!).Select(tool => tool.Name));
        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.ImportOperationsAsync(id, api, new HashSet<string> { "b" }, start, default));
        await Assert.ThrowsAsync<PanelException>(() => Catalog.ImportOperationsAsync(id, api, new HashSet<string>(), revision, default));
    }

    [Fact]
    public async Task A_proposed_base_url_is_saved_through_the_settings_path()
    {
        var id = await ServerAsync();

        await Catalog.ProposeBaseUrlAsync(id, "https://pets.example.com/api/", default);

        var settings = await Catalog.SettingsAsync(id, default);
        Assert.Equal("https://pets.example.com/api", settings.BaseUrl);
        Assert.Equal(ApiAuthKind.None, settings.AuthKind);
    }
}
