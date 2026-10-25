using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;

namespace CtxUi.IntegrationTests;

public class ApiClientsTests
{
    private static async Task<GatewayHost> StartWithEchoAsync(string url = "https://echo.example.org/mcp")
    {
        var gateway = await GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");
        await gateway.AddInboundAsync("echo", url);
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        return gateway;
    }

    private static async Task<JsonElement> IssueAsync(ApiClient api, object body)
    {
        using var response = await api.PostAsync("api/v1/clients", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync(response);
    }

    private static string Id(JsonElement issued) => issued.GetProperty("client").GetProperty("id").GetString()!;

    [Fact]
    public async Task Issued_client_comes_with_token_addresses_and_message()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.PostAsync("api/v1/clients", new
        {
            name = "Иванов 4419",
            inbounds = new[] { "learn", "echo" },
            expires = "2099-07-01",
            limits = new { ratePerMinute = 60, quotaCalls = 1000, resetEveryDays = 30 },
            metadata = new Dictionary<string, string> { ["group"] = "4419" },
        });
        var issued = await ApiClient.ReadAsync(response);
        var client = issued.GetProperty("client");
        var connection = issued.GetProperty("connection");
        var token = connection.GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/{gateway.PanelPath}/api/v1/clients/{client.GetProperty("id").GetString()}", response.Headers.Location?.OriginalString);
        Assert.True(AccessToken.IsWellFormed(token));
        Assert.Equal("Active", client.GetProperty("status").GetString());
        Assert.Equal(["echo", "learn"], client.GetProperty("inbounds").EnumerateArray().Select(slug => slug.GetString()));
        Assert.Equal(60, client.GetProperty("limits").GetProperty("ratePerMinute").GetInt32());
        Assert.Equal(JsonValueKind.Null, client.GetProperty("limits").GetProperty("maxConcurrency").ValueKind);
        Assert.Equal("2099-07-01T00:00:00+00:00", client.GetProperty("expiresAt").GetString());
        Assert.Equal("4419", client.GetProperty("metadata").GetProperty("group").GetString());
        Assert.Equal(token[..10], client.GetProperty("tokenPrefix").GetString());
        var address = connection.GetProperty("addresses")[0];
        Assert.Equal("echo", address.GetProperty("inbound").GetString());
        Assert.Equal("http://localhost:8080/mcp/echo", address.GetProperty("url").GetString());
        Assert.Equal($"http://localhost:8080/k/{token}/mcp/echo", address.GetProperty("urlWithToken").GetString());
        Assert.Contains(token, connection.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Client_issued_through_the_api_can_call_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithEchoAsync(upstream.McpUrl);
        using var api = await ApiClient.IssueAsync(gateway);

        var token = (await IssueAsync(api, new { name = "Иванов", inbounds = new[] { "echo" } }))
            .GetProperty("connection").GetProperty("token").GetString();
        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", token));

        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
    }

    [Theory]
    [InlineData("""{"inbounds":["echo"]}""", "Имя клиента не может быть пустым")]
    [InlineData("""{"name":"Иванов"}""", "Укажите хотя бы один inbound")]
    [InlineData("""{"name":"Иванов","inbounds":["nope"]}""", "Inbound не найден: nope")]
    [InlineData("""{"name":"Иванов","inbounds":["echo"],"expires":"01.07.2027"}""", "Дата «01.07.2027» не разобрана")]
    [InlineData("""{"name":"Иванов","inbounds":["echo"],"expires":"2000-01-01"}""", "Срок 2000-01-01 уже прошёл")]
    [InlineData("""{"name":"Иванов","inbounds":["echo"],"limits":{"ratePerMinute":0}}""", "Лимит запросов в минуту")]
    public async Task Invalid_client_is_refused_with_an_explanation(string body, string expected)
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.SendRawAsync(HttpMethod.Post, "api/v1/clients", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(expected, (await ApiClient.ReadAsync(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Client_is_found_by_name_in_any_case_even_with_a_slash_in_it()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var issued = await IssueAsync(api, new { name = "Группа 4419/2", inbounds = new[] { "echo" } });

        var found = await ApiClient.ReadAsync(await api.GetAsync($"api/v1/clients?name={Uri.EscapeDataString(" группа 4419/2 ")}"));
        var missing = await ApiClient.ReadAsync(await api.GetAsync("api/v1/clients?name=nobody"));
        var byId = await ApiClient.ReadAsync(await api.GetAsync($"api/v1/clients/{Id(issued)}"));

        Assert.Equal(Id(issued), Assert.Single(found.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(0, missing.GetArrayLength());
        Assert.Equal("Группа 4419/2", byId.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Patch_changes_only_what_was_sent()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var id = Id(await IssueAsync(api, new
        {
            name = "Иванов",
            inbounds = new[] { "echo" },
            expires = "2099-07-01",
            limits = new { ratePerMinute = 60 },
        }));

        using var limitsOnly = await api.PatchAsync($"api/v1/clients/{id}", new { limits = new { quotaCalls = 10 } });
        var afterLimits = await ApiClient.ReadAsync(limitsOnly);
        using var neverExpires = await api.PatchAsync($"api/v1/clients/{id}", new { expires = "never", inbounds = new[] { "learn" } });
        var afterExpiry = await ApiClient.ReadAsync(neverExpires);

        Assert.Equal(HttpStatusCode.OK, limitsOnly.StatusCode);
        Assert.Equal(JsonValueKind.Null, afterLimits.GetProperty("limits").GetProperty("ratePerMinute").ValueKind);
        Assert.Equal(10, afterLimits.GetProperty("limits").GetProperty("quotaCalls").GetInt32());
        Assert.Equal("2099-07-01T00:00:00+00:00", afterLimits.GetProperty("expiresAt").GetString());
        Assert.Equal(JsonValueKind.Null, afterExpiry.GetProperty("expiresAt").ValueKind);
        Assert.Equal("learn", Assert.Single(afterExpiry.GetProperty("inbounds").EnumerateArray()).GetString());
        Assert.Equal(10, afterExpiry.GetProperty("limits").GetProperty("quotaCalls").GetInt32());
    }

    [Fact]
    public async Task Disabled_client_is_refused_by_the_gateway_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithEchoAsync(upstream.McpUrl);
        var client = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var disable = await api.PostAsync($"api/v1/clients/{client.Client.Id}/disable");
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));
        var status = (await ApiClient.ReadAsync(await api.GetAsync($"api/v1/clients/{client.Client.Id}"))).GetProperty("status").GetString();
        using var enable = await api.PostAsync($"api/v1/clients/{client.Client.Id}/enable");
        using var allowed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));

        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Disabled", status);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Reissued_token_replaces_the_old_one_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithEchoAsync(upstream.McpUrl);
        var client = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var reissue = await api.PostAsync($"api/v1/clients/{client.Client.Id}/reissue");
        var token = (await ApiClient.ReadAsync(reissue)).GetProperty("token").GetString();
        var shown = (await ApiClient.ReadAsync(await api.GetAsync($"api/v1/clients/{client.Client.Id}/connection"))).GetProperty("token").GetString();
        using var withOld = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));
        using var withNew = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", token));

        Assert.Equal(HttpStatusCode.OK, reissue.StatusCode);
        Assert.NotEqual(client.Token, token);
        Assert.Equal(token, shown);
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task Quota_reset_starts_a_new_period()
    {
        await using var gateway = await StartWithEchoAsync();
        var client = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 5));
        using var api = await ApiClient.IssueAsync(gateway);

        using var reset = await api.PostAsync($"api/v1/clients/{client.Client.Id}/reset-quota");
        var stored = await gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync(client.Client.Id));

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.True(stored.PeriodStartedAt > client.Client.PeriodStartedAt);
    }

    [Fact]
    public async Task Removed_client_is_gone_and_unknown_ids_are_404()
    {
        await using var gateway = await StartWithEchoAsync();
        var client = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var remove = await api.DeleteAsync($"api/v1/clients/{client.Client.Id}");
        using var again = await api.DeleteAsync($"api/v1/clients/{client.Client.Id}");
        using var get = await api.GetAsync($"api/v1/clients/{client.Client.Id}");
        using var patch = await api.PatchAsync($"api/v1/clients/{client.Client.Id}", new { expires = "never" });

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal("Клиент не найден", (await ApiClient.ReadAsync(get)).GetProperty("detail").GetString());
    }

    private static async Task<(string Id, Guid DeviceId)> WithDeviceAsync(GatewayHost gateway, ApiClient api, string signedInWith = CtxUi.Core.SignIn.Device.ByKey)
    {
        var issued = await IssueAsync(api, new { name = "Иванов", inbounds = new[] { "echo" } });
        var id = Guid.Parse(Id(issued));
        var device = await gateway.ChangeAsync(async services =>
        {
            var devices = services.GetRequiredService<CtxUi.Core.SignIn.DeviceService>();
            var admitted = (await devices.AdmitAsync(
                id, CtxUi.Core.SignIn.DeviceMarker.Generate(), new CtxUi.Core.SignIn.DeviceSeen("Chrome · Windows", "203.0.113.7", signedInWith))).Device!;
            await devices.AddAppAsync(admitted.Id, "app-key", "Claude Code", "authorization-1");
            return admitted;
        });
        return (id.ToString(), device.Id);
    }

    [Fact]
    public async Task Client_view_has_device_fields()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        using var created = await api.PostAsync("api/v1/clients", new
        {
            name = "Иванов",
            inbounds = new[] { "echo" },
            access = new { maxDevices = 2, directKey = false },
        });
        var client = (await ApiClient.ReadAsync(created)).GetProperty("client");

        using var changed = await api.PatchAsync($"api/v1/clients/{client.GetProperty("id").GetString()}", new { access = new { maxDevices = 5, directKey = true } });
        var after = await ApiClient.ReadAsync(changed);

        Assert.Equal(2, client.GetProperty("maxDevices").GetInt32());
        Assert.False(client.GetProperty("directKey").GetBoolean());
        Assert.Equal(0, client.GetProperty("activeDevices").GetInt32());
        Assert.Equal(5, after.GetProperty("maxDevices").GetInt32());
        Assert.True(after.GetProperty("directKey").GetBoolean());
    }

    [Fact]
    public async Task Devices_list_and_delete()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var (id, deviceId) = await WithDeviceAsync(gateway, api);

        using var listed = await api.GetAsync($"api/v1/clients/{id}/devices");
        var device = Assert.Single((await ApiClient.ReadAsync(listed)).EnumerateArray());
        using var client = await api.GetAsync($"api/v1/clients/{id}");
        using var deleted = await api.DeleteAsync($"api/v1/clients/{id}/devices/{deviceId}");
        using var after = await api.GetAsync($"api/v1/clients/{id}/devices");
        var revoked = Assert.Single((await ApiClient.ReadAsync(after)).EnumerateArray());

        Assert.Equal("Chrome · Windows", device.GetProperty("name").GetString());
        Assert.Equal("Claude Code", Assert.Single(device.GetProperty("apps").EnumerateArray()).GetString());
        Assert.Equal("key", device.GetProperty("signedInWith").GetString());
        Assert.Equal("203.0.113.7", device.GetProperty("signInIp").GetString());
        Assert.Equal(1, (await ApiClient.ReadAsync(client)).GetProperty("activeDevices").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("Admin", revoked.GetProperty("reason").GetString());
        using var all = await api.DeleteAsync($"api/v1/clients/{id}/devices");
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
    }

    [Fact]
    public async Task Invite_returns_a_url_and_expiry()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var id = Id(await IssueAsync(api, new { name = "Иванов", inbounds = new[] { "echo" } }));

        using var invite = await api.PostAsync($"api/v1/clients/{id}/invite");
        var view = await ApiClient.ReadAsync(invite);

        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.StartsWith("http://localhost:8080/connect/invite/", view.GetProperty("url").GetString());
        Assert.InRange(view.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
    }

    [Fact]
    public async Task Links_list_and_delete()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var id = Id(await IssueAsync(api, new { name = "Иванов", inbounds = new[] { "echo" } }));
        await gateway.ChangeAsync(async services =>
        {
            var links = services.GetRequiredService<CtxUi.Core.SignIn.ClientLinkService>();
            await links.LinkAsync(await links.InviteAsync(Guid.Parse(id)), new CtxUi.Core.SignIn.ExternalIdentity("github", "42", "ivanov@example.org"));
            return true;
        });

        using var listed = await api.GetAsync($"api/v1/clients/{id}/links");
        var link = Assert.Single((await ApiClient.ReadAsync(listed)).EnumerateArray());
        using var removed = await api.DeleteAsync($"api/v1/clients/{id}/links/github");
        using var again = await api.DeleteAsync($"api/v1/clients/{id}/links/github");

        Assert.Equal("github", link.GetProperty("provider").GetString());
        Assert.Equal("ivanov@example.org", link.GetProperty("display").GetString());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Reissue_reports_revoked_devices()
    {
        await using var gateway = await StartWithEchoAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        var (id, _) = await WithDeviceAsync(gateway, api);

        using var reissued = await api.PostAsync($"api/v1/clients/{id}/reissue");

        Assert.Equal(1, (await ApiClient.ReadAsync(reissued)).GetProperty("devicesRevoked").GetInt32());
    }
}
