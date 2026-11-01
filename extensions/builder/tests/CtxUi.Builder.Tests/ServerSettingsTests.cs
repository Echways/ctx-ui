using CtxUi.Builder.Contracts;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Tests;

public sealed class ServerSettingsTests : IClassFixture<CatalogHarness>
{
    private readonly CatalogHarness _harness;

    public ServerSettingsTests(CatalogHarness harness) => _harness = harness;

    private ServerCatalog Catalog => _harness.Catalog;

    private static string Slug() => "s" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Api_settings_are_stored_and_the_secret_never_comes_back()
    {
        var server = await Catalog.CreateAsync(Slug(), "Api", default);
        await Catalog.SetApiAsync(server.Id, "https://api.example.com/v1", new ApiAuthInput(ApiAuthKind.Bearer, Secret: "tkn-123"), 0, default);

        var settings = await Catalog.SettingsAsync(server.Id, default);

        Assert.Equal("https://api.example.com/v1", settings.BaseUrl);
        Assert.Equal(ApiAuthKind.Bearer, settings.AuthKind);
        Assert.True(settings.AuthHasSecret);
        Assert.DoesNotContain("tkn-123", settings.ToString());
        var stored = (await _harness.Inspect().Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == server.Id)).ApiAuthSealed;
        Assert.NotNull(stored);
        Assert.DoesNotContain("tkn-123", stored);
        Assert.Contains("tkn-123", _harness.Box.Open(stored));
    }

    [Fact]
    public async Task Blank_secret_keeps_the_stored_one_when_the_kind_is_unchanged()
    {
        var server = await Catalog.CreateAsync(Slug(), "Keep", default);
        var revision = await Catalog.SetApiAsync(server.Id, "https://api.example.com", new ApiAuthInput(ApiAuthKind.Header, "X-Api-Key", Secret: "first"), 0, default);
        var before = (await _harness.Inspect().Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == server.Id)).ApiAuthSealed!;

        await Catalog.SetApiAsync(server.Id, "https://api.example.com", new ApiAuthInput(ApiAuthKind.Header, "X-Api-Key", Secret: ""), revision, default);

        var after = (await _harness.Inspect().Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == server.Id)).ApiAuthSealed!;
        Assert.Equal(_harness.Box.Open(before), _harness.Box.Open(after));
    }

    [Fact]
    public async Task Changing_the_kind_without_a_secret_is_refused()
    {
        var server = await Catalog.CreateAsync(Slug(), "Kind", default);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SetApiAsync(server.Id, null, new ApiAuthInput(ApiAuthKind.Bearer), 0, default));
    }

    [Fact]
    public async Task No_auth_clears_the_stored_secret()
    {
        var server = await Catalog.CreateAsync(Slug(), "Clear", default);
        var revision = await Catalog.SetApiAsync(server.Id, null, new ApiAuthInput(ApiAuthKind.Bearer, Secret: "x"), 0, default);
        await Catalog.SetApiAsync(server.Id, null, new ApiAuthInput(ApiAuthKind.None), revision, default);

        var settings = await Catalog.SettingsAsync(server.Id, default);
        Assert.False(settings.AuthHasSecret);
        Assert.Null((await _harness.Inspect().Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == server.Id)).ApiAuthSealed);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    public async Task Base_url_must_be_an_absolute_http_address(string url)
    {
        var server = await Catalog.CreateAsync(Slug(), "Url", default);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SetApiAsync(server.Id, url, new ApiAuthInput(ApiAuthKind.None), 0, default));
    }

    [Theory]
    [InlineData("bad header", "v")]
    [InlineData("", "v")]
    public async Task Header_name_must_be_a_token(string header, string secret)
    {
        var server = await Catalog.CreateAsync(Slug(), "Header", default);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SetApiAsync(server.Id, null, new ApiAuthInput(ApiAuthKind.Header, header, Secret: secret), 0, default));
    }

    [Fact]
    public async Task Dependencies_are_normalised_and_installer_options_refused()
    {
        var server = await Catalog.CreateAsync(Slug(), "Deps", default);
        await Catalog.SetDependenciesAsync(server.Id, "httpx>=0.28\n\n fastmcp==1.0 \npydantic\nhttpx>=0.28", 0, default);

        Assert.Equal("httpx>=0.28\npydantic", (await Catalog.SettingsAsync(server.Id, default)).Dependencies);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SetDependenciesAsync(server.Id, "--index-url https://x", 1, default));
    }

    [Fact]
    public async Task Settings_list_secret_names_only()
    {
        var server = await Catalog.CreateAsync(Slug(), "Names", default);
        await Catalog.SetSecretAsync(server.Id, "API_KEY", "super-secret", default);

        var settings = await Catalog.SettingsAsync(server.Id, default);

        Assert.Equal(["API_KEY"], settings.SecretNames);
        Assert.DoesNotContain("super-secret", settings.ToString());
    }

    [Fact]
    public async Task Imported_archive_creates_a_server_with_its_files_draft_and_dependencies()
    {
        var import = new ProjectImport(
            [("mcp/a.py", "a = 1"), ("openapi.json", "{\"openapi\": \"3.1.0\"}"), ("builder.json", "{}")],
            "httpx");

        var server = await Catalog.ImportAsync(Slug(), "Imported", import, default);

        var draft = await Catalog.DraftAsync(server.Id, default);
        Assert.Equal("mcp/a.py", Assert.Single(draft.Files).Path);
        Assert.Equal("{\"openapi\": \"3.1.0\"}", draft.OpenApiJson);
        Assert.Equal("{}", draft.BuilderJson);
        Assert.Equal("httpx", draft.Dependencies);
    }

    [Fact]
    public async Task Import_with_a_taken_slug_writes_nothing()
    {
        var slug = Slug();
        await Catalog.CreateAsync(slug, "First", default);

        await Assert.ThrowsAsync<PanelException>(() => Catalog.ImportAsync(slug, "Second", new ProjectImport([("mcp/a.py", "a")], null), default));

        Assert.Single(await Catalog.ListAsync(default), server => server.Slug == slug);
        Assert.Empty(await _harness.Inspect().Files.Where(file => file.Content == "a").ToListAsync());
    }

    [Fact]
    public async Task Saving_a_file_with_a_disallowed_type_is_refused()
    {
        var server = await Catalog.CreateAsync(Slug(), "Types", default);
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFileAsync(server.Id, "mcp/a.exe", "x", 0, default));
        await Assert.ThrowsAsync<PanelException>(() => Catalog.SaveFileAsync(server.Id, "openapi.json", "{}", 0, default));
    }

    [Fact]
    public async Task Exported_project_is_a_tar_archive_named_after_the_slug()
    {
        var slug = Slug();
        var server = await Catalog.CreateAsync(slug, "Export", default);
        await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "a = 1", 0, default);

        var export = await Catalog.ExportAsync(server.Id, default);

        Assert.Equal($"{slug}.tar.gz", export.FileName);
        var import = await ProjectFiles.ImportArchiveAsync(new MemoryStream(export.Content), default);
        Assert.Equal("mcp/a.py", Assert.Single(import.Files).Path);
    }

    [Fact]
    public async Task Refused_archive_creates_no_server()
    {
        var slug = Slug();
        var archive = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(archive, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = zip.CreateEntry("mcp/../evil.py").Open();
            writer.WriteByte((byte)'x');
        }

        archive.Position = 0;

        await Assert.ThrowsAsync<PanelException>(() => Catalog.ImportArchiveAsync(slug, "Bad", archive, default));
        Assert.DoesNotContain(await Catalog.ListAsync(default), server => server.Slug == slug);
    }
}
