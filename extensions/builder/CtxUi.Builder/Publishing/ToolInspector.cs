using System.Diagnostics;
using System.Text.Json.Nodes;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CtxUi.Builder.Publishing;

public sealed class ToolInspector(RunnerSettings settings, IHttpClientFactory http, ServerCatalog catalog)
    : IToolInspector
{
    private static readonly string[] ProxyRefusalMarkers =
        ["ERR_ACCESS_DENIED", "ERR_ACL", "X-Squid-Error", "squid", "The requested URL could not be retrieved"];

    public static bool IsProxyRefusal(string text)
    {
        return ProxyRefusalMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
               || (text.Contains("proxy", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains("403", StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<ToolInfo>> ListAsync(Guid serverId, int? release, CancellationToken ct)
    {
        await using var client = await ConnectAsync(serverId, release, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Select(tool => new ToolInfo(tool.Name, tool.Description, tool.JsonSchema.Clone()))];
    }

    public async Task<ToolCallOutcome> CallAsync(Guid serverId, int? release, string tool, JsonObject arguments,
        CancellationToken ct)
    {
        await using var client = await ConnectAsync(serverId, release, ct);
        var values = arguments.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await client.CallToolAsync(tool, values, cancellationToken: ct);
            var text = TextOf(result);
            var failed = result.IsError == true;
            return new ToolCallOutcome(failed, text, timer.Elapsed, failed && IsProxyRefusal(text));
        }
        catch (McpException failure)
        {
            return new ToolCallOutcome(true, failure.Message, timer.Elapsed, IsProxyRefusal(failure.Message));
        }
    }

    private async Task<McpClient> ConnectAsync(Guid serverId, int? release, CancellationToken ct)
    {
        if (!settings.IsComplete)
            throw new BuilderException(
                string.Format(BuilderText.RunnerNotConfigured, string.Join(", ", settings.Missing)));

        var secret = await catalog.RouteSecretAsync(serverId, ct);
        var path = release is null ? $"s/{serverId}/mcp" : $"s/{serverId}/r/{release}/mcp";
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(settings.IngressUrl!, path),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { [RouteSecret.Header] = secret }
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, http.CreateClient(), null, true),
            cancellationToken: ct);
    }

    private static string TextOf(CallToolResult result)
    {
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return text.Length > 0 || result.StructuredContent is null ? text : result.StructuredContent.Value.GetRawText();
    }
}
