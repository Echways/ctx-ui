using System.Text;
using System.Text.Json.Nodes;
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
    public async Task Files_and_the_api_document_share_the_project_size_limit()
    {
        var id = await ServerAsync();
        var half = (int)(ProjectFiles.MaxProjectBytes / 2);
        var document = JsonNode.Parse(OpenApiDraft.Upsert(string.Empty, Tool()))!;
        document["x-padding"] = new string('a', half);
        await Catalog.SaveOpenApiAsync(id, document.ToJsonString(), (await Catalog.DraftAsync(id, default)).Revision, default);

        var failure = await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFileAsync(id, "mcp/data.txt", new string('b', half + 1), 0, default));

        Assert.Equal(BuilderText.ProjectTooLarge, failure.Message);
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

    [Fact]
    public async Task A_new_form_tool_cannot_take_the_name_of_a_form_tool_or_a_code_tool()
    {
        var id = await ServerAsync();
        var revision = await Catalog.SaveFormToolAsync(id, Tool("taken"), true, (await Catalog.DraftAsync(id, default)).Revision, default);

        var duplicate = await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFormToolAsync(id, Tool("taken") with { Description = "other" }, true, revision, default));
        Assert.Equal(string.Format(BuilderText.ToolNameTaken, "taken"), duplicate.Message);

        await Catalog.SaveFileAsync(id, "mcp/greet.py", "from fastmcp.tools import tool\n\n\n@tool\ndef greet():\n    return 1\n", 0, default);
        revision = (await Catalog.DraftAsync(id, default)).Revision;
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFormToolAsync(id, Tool("greet"), true, revision, default));

        var edited = await Catalog.SaveFormToolAsync(id, Tool("taken") with { Description = "edited" }, false, revision, default);
        Assert.Equal(revision + 1, edited);
        Assert.Equal("edited", Assert.Single(OpenApiDraft.Read((await Catalog.DraftAsync(id, default)).OpenApiJson!)).Description);
    }

    [Fact]
    public async Task Importing_a_name_used_by_a_code_tool_is_refused_with_the_name()
    {
        var id = await ServerAsync();
        await Catalog.SaveFileAsync(id, "mcp/a.py", "@tool\ndef a():\n    return 1\n", 0, default);
        var document = """{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"operationId":"a","responses":{"200":{"description":"OK"}}}}}}""";
        var api = await OpenApiImport.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(document)), default);

        var failure = await Assert.ThrowsAsync<PanelException>(async () => await Catalog.ImportOperationsAsync(id, api, new HashSet<string> { "a" }, (await Catalog.DraftAsync(id, default)).Revision, default));

        Assert.Equal(string.Format(BuilderText.ImportNameTaken, "a"), failure.Message);
        Assert.Null((await Catalog.DraftAsync(id, default)).OpenApiJson);
    }

    [Fact]
    public async Task A_conflicting_delete_leaves_the_document_and_a_retry_at_the_fresh_revision_deletes_it()
    {
        var id = await ServerAsync();
        var first = await Catalog.SaveFormToolAsync(id, Tool("one"), true, (await Catalog.DraftAsync(id, default)).Revision, default);
        await Catalog.SaveFormToolAsync(id, Tool("two"), true, first, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.DeleteFormToolAsync(id, "one", first, default));
        Assert.Equal(2, OpenApiDraft.Read((await Catalog.DraftAsync(id, default)).OpenApiJson!).Count);

        await Catalog.DeleteFormToolAsync(id, "one", (await Catalog.DraftAsync(id, default)).Revision, default);

        Assert.Equal(["two"], OpenApiDraft.Read((await Catalog.DraftAsync(id, default)).OpenApiJson!).Select(tool => tool.Name));
    }

    [Fact]
    public async Task A_conflicting_convert_changes_nothing()
    {
        var id = await ServerAsync();
        var first = await Catalog.SaveFormToolAsync(id, Tool("one"), true, (await Catalog.DraftAsync(id, default)).Revision, default);
        await Catalog.SaveFormToolAsync(id, Tool("two"), true, first, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.ConvertToCodeAsync(id, "one", first, default));

        var draft = await Catalog.DraftAsync(id, default);
        Assert.Empty(draft.Files);
        Assert.Equal(2, OpenApiDraft.Read(draft.OpenApiJson!).Count);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("""{"hello":"world"}""")]
    [InlineData("""{"swagger":"2.0","info":{"title":"t","version":"1"},"paths":{}}""")]
    public async Task An_archive_with_an_unusable_openapi_json_is_refused(string document)
    {
        var import = new ProjectImport([("openapi.json", document)], null);

        var failure = await Assert.ThrowsAsync<PanelException>(() => Catalog.ImportAsync(Slug(), "Archive", import, default));

        Assert.StartsWith(BuilderText.ArchiveOpenApiInvalid.Split("{0}")[0], failure.Message);
    }

    [Fact]
    public async Task A_stale_edit_cannot_recreate_a_form_tool_that_was_turned_into_code()
    {
        var id = await ServerAsync();
        var revision = await Catalog.SaveFormToolAsync(id, Tool("move"), true, (await Catalog.DraftAsync(id, default)).Revision, default);
        var (_, converted) = await Catalog.ConvertToCodeAsync(id, "move", revision, default);

        var failure = await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFormToolAsync(id, Tool("move"), false, converted, default));

        Assert.Equal(string.Format(BuilderText.ToolNameTaken, "move"), failure.Message);
    }

    [Fact]
    public async Task A_code_tool_cannot_take_the_name_of_a_form_tool()
    {
        var id = await ServerAsync();
        await Catalog.SaveFormToolAsync(id, Tool("forecast"), true, (await Catalog.DraftAsync(id, default)).Revision, default);

        var failure = await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFileAsync(id, "mcp/f.py", "@tool\ndef forecast():\n    pass\n", 0, default));
        Assert.Equal(string.Format(BuilderText.CodeToolNameTaken, "forecast"), failure.Message);
        Assert.Empty((await Catalog.DraftAsync(id, default)).Files);

        var revision = await Catalog.SaveFileAsync(id, "mcp/f.py", "@tool\ndef other():\n    pass\n", 0, default);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFileAsync(id, "mcp/f.py", "@tool(name=\"forecast\")\ndef other():\n    pass\n", revision, default));
        await Catalog.SaveFileAsync(id, "mcp/data.txt", "forecast", 0, default);
    }
}
