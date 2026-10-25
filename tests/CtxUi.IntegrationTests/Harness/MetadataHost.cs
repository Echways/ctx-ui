using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using CtxUi.Connect.Registration;

namespace CtxUi.IntegrationTests;

public sealed class MetadataHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MetadataHost(WebApplication app) => _app = app;

    public string Document { get; set; } = "";

    public int Fetches { get; private set; }

    public string Url => _app.Urls.First() + "/client.json";

    public static async Task<MetadataHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var host = new MetadataHost(app);
        app.MapGet("/client.json", (HttpContext context) =>
        {
            host.Fetches++;
            context.Response.Headers.CacheControl = "max-age=600";
            return Results.Text(host.Document, "application/json");
        });
        await app.StartAsync();
        return host;
    }

    public string For(params string[] redirectUris) =>
        Document = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["client_id"] = Url,
            ["client_name"] = "Claude Code",
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
        });

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public sealed class LoopbackMetadataPolicy : IMetadataAddressPolicy
{
    public bool AllowsUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.AbsolutePath != "/";

    public bool AllowsAddress(IPAddress address) => IPAddress.IsLoopback(address);
}
