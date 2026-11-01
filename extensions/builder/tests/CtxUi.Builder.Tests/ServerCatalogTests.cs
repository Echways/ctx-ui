using System.Text.Json;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Servers;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Tests;

public sealed class ServerCatalogTests : IClassFixture<CatalogHarness>
{
    private readonly CatalogHarness _harness;

    public ServerCatalogTests(CatalogHarness harness) => _harness = harness;

    private ServerCatalog Catalog => _harness.Catalog;

    private static string Slug() => "s" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task New_server_has_internet_only_access_and_default_limits()
    {
        var server = await Catalog.CreateAsync(Slug(), "Weather", default);
        Assert.Equal(NetworkAccess.InternetOnly, server.NetworkAccess);
        Assert.Equal((512, 1d, 256), (server.MemoryMb, server.CpuLimit, server.PidsLimit));
        Assert.Null(server.ActiveReleaseId);
        Assert.Null(server.InboundId);
    }

    [Fact]
    public async Task Created_server_is_listed_with_its_empty_draft()
    {
        var slug = Slug();
        var server = await Catalog.CreateAsync(slug, "Listed", default);

        Assert.Contains(await Catalog.ListAsync(default), listed => listed.Id == server.Id && listed.Slug == slug);
        var draft = await Catalog.DraftAsync(server.Id, default);
        Assert.Equal(server.Id, draft.ServerId);
        Assert.Empty(draft.Files);
    }

    [Fact]
    public async Task Taken_server_slug_is_refused()
    {
        var slug = Slug();
        await Catalog.CreateAsync(slug, "First", default);
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.CreateAsync(slug, "Second", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-dash")]
    [InlineData("has space")]
    public async Task Invalid_server_slug_is_refused(string slug)
    {
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.CreateAsync(slug, "Name", default));
    }

    [Fact]
    public async Task Blank_server_name_is_refused()
    {
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.CreateAsync(Slug(), "  ", default));
    }

    [Fact]
    public async Task Saved_file_starts_at_revision_one_and_moves_forward()
    {
        var server = await Catalog.CreateAsync(Slug(), "Files", default);

        var first = await Catalog.SaveFileAsync(server.Id, "mcp/tools/hello.py", "a", 0, default);
        var second = await Catalog.SaveFileAsync(server.Id, "mcp/tools/hello.py", "b", first, default);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        var draft = await Catalog.DraftAsync(server.Id, default);
        var file = Assert.Single(draft.Files);
        Assert.Equal(("mcp/tools/hello.py", "b", 2L), (file.Path, file.Content, file.Revision));
        Assert.Equal(2, draft.Revision);
    }

    [Fact]
    public async Task Saving_over_a_newer_revision_throws_conflict_and_keeps_the_newer_content()
    {
        var server = await Catalog.CreateAsync(Slug(), "Conflict", default);
        var first = await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "one", 0, default);
        await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "newer", first, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.SaveFileAsync(server.Id, "mcp/a.py", "stale", first, default));

        var draft = await Catalog.DraftAsync(server.Id, default);
        Assert.Equal("newer", Assert.Single(draft.Files).Content);
    }

    [Fact]
    public async Task Creating_a_file_that_already_exists_throws_conflict()
    {
        var server = await Catalog.CreateAsync(Slug(), "Exists", default);
        await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "one", 0, default);

        await Assert.ThrowsAsync<DraftConflictException>(() => Catalog.SaveFileAsync(server.Id, "mcp/a.py", "two", 0, default));
    }

    [Fact]
    public async Task Concurrent_saves_of_one_revision_let_exactly_one_win()
    {
        var server = await Catalog.CreateAsync(Slug(), "Race", default);
        var first = await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "one", 0, default);
        var other = _harness.AnotherCatalog();

        await Catalog.SaveFileAsync(server.Id, "mcp/a.py", "left", first, default);
        await Assert.ThrowsAsync<DraftConflictException>(() => other.SaveFileAsync(server.Id, "mcp/a.py", "right", first, default));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("mcp/../secret.py")]
    [InlineData("other/a.py")]
    [InlineData("mcp\\a.py")]
    [InlineData("mcp/")]
    [InlineData("mcp//a.py")]
    [InlineData("")]
    public async Task Unsafe_file_paths_are_refused(string path)
    {
        var server = await Catalog.CreateAsync(Slug(), "Paths", default);
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.SaveFileAsync(server.Id, path, "x", 0, default));
    }

    [Fact]
    public async Task Project_larger_than_sixteen_megabytes_is_refused()
    {
        var server = await Catalog.CreateAsync(Slug(), "Large", default);
        var half = new string('x', 9 * 1024 * 1024);
        await Catalog.SaveFileAsync(server.Id, "mcp/a.py", half, 0, default);

        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.SaveFileAsync(server.Id, "mcp/b.py", half, 0, default));
    }

    [Fact]
    public async Task Saving_to_an_unknown_server_is_refused()
    {
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.SaveFileAsync(Guid.NewGuid(), "mcp/a.py", "x", 0, default));
    }

    [Fact]
    public async Task Secret_is_stored_sealed()
    {
        var server = await Catalog.CreateAsync(Slug(), "Secrets", default);
        await Catalog.SetSecretAsync(server.Id, "API_KEY", "plain-secret-value", default);

        await using var inspect = _harness.Inspect();
        var stored = await inspect.Secrets.SingleAsync(secret => secret.ServerId == server.Id);
        Assert.Equal("API_KEY", stored.Name);
        Assert.NotEqual("plain-secret-value", stored.ValueSealed);
        Assert.DoesNotContain("plain-secret-value", stored.ValueSealed);
        Assert.Equal("plain-secret-value", _harness.Box.Open(stored.ValueSealed));
    }

    [Fact]
    public async Task Setting_a_secret_again_replaces_its_value()
    {
        var server = await Catalog.CreateAsync(Slug(), "Replace", default);
        await Catalog.SetSecretAsync(server.Id, "API_KEY", "first", default);
        await Catalog.SetSecretAsync(server.Id, "API_KEY", "second", default);

        await using var inspect = _harness.Inspect();
        var stored = await inspect.Secrets.SingleAsync(secret => secret.ServerId == server.Id);
        Assert.Equal("second", _harness.Box.Open(stored.ValueSealed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1BAD")]
    [InlineData("has space")]
    [InlineData("a-b")]
    public async Task Invalid_secret_names_are_refused(string name)
    {
        var server = await Catalog.CreateAsync(Slug(), "Names", default);
        await Assert.ThrowsAsync<Enfilade.PanelException>(() => Catalog.SetSecretAsync(server.Id, name, "v", default));
    }

    [Fact]
    public async Task Secret_value_is_never_returned_by_the_catalog()
    {
        var server = await Catalog.CreateAsync(Slug(), "Hidden", default);
        await Catalog.SetSecretAsync(server.Id, "API_KEY", "plain-secret-value", default);

        var listed = JsonSerializer.Serialize(await Catalog.ListAsync(default));
        var draft = JsonSerializer.Serialize(await Catalog.DraftAsync(server.Id, default));

        Assert.DoesNotContain("plain-secret-value", listed);
        Assert.DoesNotContain("plain-secret-value", draft);
    }

    [Fact]
    public async Task New_server_gets_a_sealed_route_secret()
    {
        var server = await Catalog.CreateAsync(Slug(), "Route", default);

        var routeSecret = _harness.Box.Open(server.RouteSecretSealed);
        Assert.True(routeSecret.Length >= 32);
        Assert.NotEqual(routeSecret, server.RouteSecretSealed);
    }

    [Fact]
    public async Task Access_and_limits_are_updated()
    {
        var server = await Catalog.CreateAsync(Slug(), "Access", default);

        await Catalog.SetAccessAsync(server.Id, NetworkAccess.FullNetwork, new ServerLimits(1024, 2, 512), default);

        var updated = Assert.Single(await Catalog.ListAsync(default), listed => listed.Id == server.Id);
        Assert.Equal(NetworkAccess.FullNetwork, updated.NetworkAccess);
        Assert.Equal((1024, 2d, 512), (updated.MemoryMb, updated.CpuLimit, updated.PidsLimit));
    }

    [Theory]
    [InlineData(5, 1, 256)]
    [InlineData(512, 0, 256)]
    [InlineData(512, double.NaN, 256)]
    [InlineData(512, 1, 0)]
    public async Task Unsafe_limits_are_refused(int memory, double cpus, int pids)
    {
        var server = await Catalog.CreateAsync(Slug(), "Limits", default);
        await Assert.ThrowsAsync<Enfilade.PanelException>(() =>
            Catalog.SetAccessAsync(server.Id, NetworkAccess.InternetOnly, new ServerLimits(memory, cpus, pids), default));
    }
}
