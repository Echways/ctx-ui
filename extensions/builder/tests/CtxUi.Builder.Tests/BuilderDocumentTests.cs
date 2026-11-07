using System.Text.Json.Nodes;
using CtxUi.Builder.Servers;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class BuilderDocumentTests
{
    private static BuilderResource Cities()
    {
        return new BuilderResource("data://cities", "cities", "application/json", "[\"Berlin\"]");
    }

    private static BuilderPrompt Report()
    {
        return new BuilderPrompt("daily_report", "Daily report", "Отчёт за {day}");
    }

    [Fact]
    public void Prompt_arguments_are_taken_from_the_template()
    {
        var arguments =
            BuilderDocument.Arguments("Report for {day} in {city}, again {day}, not {1x} or {} or {a b} or {{}");

        Assert.Equal(["day", "city"], arguments);
    }

    [Fact]
    public void Duplicate_resource_uri_is_refused()
    {
        var model = new BuilderDocumentModel([Cities(), Cities() with { Name = "other" }], []);

        Assert.Throws<BuilderException>(() => BuilderDocument.Write(model));
        Assert.Throws<BuilderException>(() =>
            BuilderDocument.WithResource(new BuilderDocumentModel([Cities()], []), Cities(), null));
        Assert.Throws<BuilderException>(() =>
            BuilderDocument.WithResource(
                new BuilderDocumentModel([Cities(), Cities() with { Uri = "data://towns" }], []),
                Cities() with { Uri = "data://towns" }, "data://cities"));
    }

    [Theory]
    [InlineData("/cities")]
    [InlineData("cities")]
    [InlineData("1data://cities")]
    [InlineData("://cities")]
    [InlineData("data://my cities")]
    public void Resource_uri_without_an_explicit_scheme_is_refused(string uri)
    {
        var model = new BuilderDocumentModel([Cities() with { Uri = uri }], []);

        Assert.Throws<BuilderException>(() => BuilderDocument.Write(model));
    }

    [Fact]
    public void Duplicate_prompt_name_is_refused()
    {
        Assert.Throws<BuilderException>(() => BuilderDocument.Write(new BuilderDocumentModel([], [Report(), Report()])));
    }

    [Fact]
    public void Written_document_uses_the_field_names_the_runtime_reads()
    {
        var json = BuilderDocument.Write(new BuilderDocumentModel([Cities()], [Report()]));

        var root = JsonNode.Parse(json)!;
        var resource = root["resources"]![0]!;
        Assert.Equal(["uri", "name", "mimeType", "text"], resource.AsObject().Select(pair => pair.Key));
        Assert.Equal("data://cities", (string?)resource["uri"]);
        Assert.Equal("application/json", (string?)resource["mimeType"]);
        var prompt = root["prompts"]![0]!;
        Assert.Equal(["name", "description", "template"], prompt.AsObject().Select(pair => pair.Key));
        Assert.Equal("Отчёт за {day}", (string?)prompt["template"]);
        Assert.Contains("Отчёт", json);
    }

    [Fact]
    public void Document_survives_a_round_trip()
    {
        var model = new BuilderDocumentModel([Cities()], [Report(), new BuilderPrompt("plain", null, "Hello")]);

        var read = BuilderDocument.Read(BuilderDocument.Write(model));

        Assert.Equal(model.Resources, read.Resources);
        Assert.Equal(model.Prompts, read.Prompts);
    }

    [Fact]
    public void Missing_mime_type_and_text_get_the_runtime_defaults()
    {
        var read = BuilderDocument.Read("""{"resources":[{"uri":"data://a","name":"a"}]}""");

        var resource = Assert.Single(read.Resources);
        Assert.Equal("text/plain", resource.MimeType);
        Assert.Equal(string.Empty, resource.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Blank_document_reads_as_empty(string json)
    {
        var read = BuilderDocument.Read(json);

        Assert.Empty(read.Resources);
        Assert.Empty(read.Prompts);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"resources":{}}""")]
    [InlineData("""{"resources":[{"uri":"data://x"}]}""")]
    [InlineData("""{"resources":[{"uri":"not a uri","name":"x"}]}""")]
    [InlineData("""{"resources":[{"uri":"data://x","name":"x","text":5}]}""")]
    [InlineData("""{"prompts":[{"name":"p"}]}""")]
    [InlineData("""{"prompts":[{"name":"","template":"t"}]}""")]
    [InlineData("""{"prompts":[{"name":"p","template":"a"},{"name":"p","template":"b"}]}""")]
    public void Broken_document_is_refused(string json)
    {
        Assert.Throws<BuilderException>(() => BuilderDocument.Read(json));
    }

    [Fact]
    public void Renaming_a_resource_keeps_its_position()
    {
        var model = new BuilderDocumentModel([Cities(), Cities() with { Uri = "data://towns", Name = "towns" }], []);

        var renamed = BuilderDocument.WithResource(model, Cities() with { Uri = "data://places" }, "data://cities");

        Assert.Equal(["data://places", "data://towns"], renamed.Resources.Select(resource => resource.Uri));
    }

    [Fact]
    public void Removing_the_last_entries_leaves_an_empty_model()
    {
        var model = new BuilderDocumentModel([Cities()], [Report()]);

        var emptied =
            BuilderDocument.WithoutPrompt(BuilderDocument.WithoutResource(model, "data://cities"), "daily_report");

        Assert.Empty(emptied.Resources);
        Assert.Empty(emptied.Prompts);
    }
}

public sealed class BuilderDraftTests : IClassFixture<CatalogHarness>
{
    private readonly CatalogHarness _harness;

    public BuilderDraftTests(CatalogHarness harness)
    {
        _harness = harness;
    }

    private ServerCatalog Catalog => _harness.Catalog;

    private static string Slug()
    {
        return "s" + Guid.NewGuid().ToString("N")[..12];
    }

    [Fact]
    public async Task Saved_resource_and_prompt_are_stored_in_the_builder_document()
    {
        var server = await Catalog.CreateAsync(Slug(), "Docs", default);

        var revision = await Catalog.SaveResourceAsync(server.Id,
            new BuilderResource("data://cities", "cities", "text/plain", "Berlin"), null, 0, default);
        revision = await Catalog.SavePromptAsync(server.Id, new BuilderPrompt("daily_report", null, "Report {day}"),
            null, revision, default);

        var draft = await Catalog.DraftAsync(server.Id, default);
        var document = BuilderDocument.Read(draft.BuilderJson!);
        Assert.Equal(revision, draft.Revision);
        Assert.Equal("data://cities", Assert.Single(document.Resources).Uri);
        Assert.Equal("daily_report", Assert.Single(document.Prompts).Name);
    }

    [Fact]
    public async Task Stale_revision_raises_a_conflict_and_keeps_the_other_admins_entry()
    {
        var server = await Catalog.CreateAsync(Slug(), "Race", default);
        await Catalog.SaveResourceAsync(server.Id, new BuilderResource("data://a", "a", "text/plain", "theirs"), null,
            0, default);

        await Assert.ThrowsAsync<DraftConflictException>(() =>
            Catalog.SaveResourceAsync(server.Id, new BuilderResource("data://a", "a", "text/plain", "mine"), null, 0,
                default));
        await Assert.ThrowsAsync<DraftConflictException>(() =>
            Catalog.SavePromptAsync(server.Id, new BuilderPrompt("p", null, "t"), null, 0, default));
        await Assert.ThrowsAsync<DraftConflictException>(() =>
            Catalog.DeleteResourceAsync(server.Id, "data://a", 0, default));

        var document = BuilderDocument.Read((await Catalog.DraftAsync(server.Id, default)).BuilderJson!);
        Assert.Equal("theirs", Assert.Single(document.Resources).Text);
    }

    [Fact]
    public async Task Deleting_the_last_entry_removes_the_document()
    {
        var server = await Catalog.CreateAsync(Slug(), "Empty", default);
        var revision = await Catalog.SavePromptAsync(server.Id, new BuilderPrompt("p", null, "t"), null, 0, default);

        await Catalog.DeletePromptAsync(server.Id, "p", revision, default);

        Assert.Null((await Catalog.DraftAsync(server.Id, default)).BuilderJson);
    }

    [Fact]
    public async Task Saving_for_an_unknown_server_is_refused()
    {
        await Assert.ThrowsAsync<BuilderException>(() => Catalog.SaveResourceAsync(Guid.NewGuid(),
            new BuilderResource("data://a", "a", "text/plain", ""), null, 0, default));
    }

    [Fact]
    public async Task Archive_with_a_broken_builder_document_creates_no_server()
    {
        var slug = Slug();
        var import = new ProjectImport([("builder.json", "{broken")], null);

        await Assert.ThrowsAsync<BuilderException>(() => Catalog.ImportAsync(slug, "Bad", import, default));

        Assert.DoesNotContain(await Catalog.ListAsync(default), server => server.Slug == slug);
    }
}
