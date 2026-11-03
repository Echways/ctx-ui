using System.Text.Json;
using System.Text.Json.Nodes;

namespace CtxUi.Builder.Publishing;

public sealed record ToolInfo(string Name, string? Description, JsonElement InputSchema);

public sealed record ToolCallOutcome(bool IsError, string Text, TimeSpan Elapsed, bool ProxyRefused);

public interface IToolInspector
{
    Task<IReadOnlyList<ToolInfo>> ListAsync(Guid serverId, int? release, CancellationToken ct);

    Task<ToolCallOutcome> CallAsync(Guid serverId, int? release, string tool, JsonObject arguments, CancellationToken ct);
}
