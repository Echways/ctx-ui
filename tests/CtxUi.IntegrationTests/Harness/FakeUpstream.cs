using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CtxUi.IntegrationTests;

public sealed record RecordedRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

public sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeUpstream(WebApplication app) => _app = app;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public string BaseUrl => _app.Urls.First();

    public string McpUrl => BaseUrl + "/mcp";

    public static async Task<FakeUpstream> StartAsync(Action<WebApplication, FakeUpstream>? map = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var upstream = new FakeUpstream(app);
        app.Map("/mcp", async context =>
        {
            await upstream.RecordAsync(context);
            context.Response.Headers["Mcp-Session-Id"] = "session-1";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        });
        map?.Invoke(app, upstream);
        await app.StartAsync();
        return upstream;
    }

    public async Task<RecordedRequest> RecordAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var recorded = new RecordedRequest(
            context.Request.Method,
            context.Request.Path.Value ?? "",
            context.Request.QueryString.Value ?? "",
            context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            await reader.ReadToEndAsync());
        Requests.Enqueue(recorded);
        return recorded;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
