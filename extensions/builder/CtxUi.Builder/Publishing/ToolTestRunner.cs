using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace CtxUi.Builder.Publishing;

public sealed record ToolTarget(int Number, int? Route, bool IsCandidate);

public sealed class ToolTestRunner(IToolInspector inspector, ReleaseCatalog releases)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public Task<ToolTarget?> TargetAsync(Guid serverId, CancellationToken ct)
    {
        return releases.TestTargetAsync(serverId, ct);
    }

    public Task<IReadOnlyList<ToolInfo>> ListAsync(Guid serverId, ToolTarget target, CancellationToken ct)
    {
        return GuardedAsync(token => inspector.ListAsync(serverId, target.Route, token), ct);
    }

    public Task<ToolCallOutcome> CallAsync(Guid serverId, ToolTarget target, string tool, JsonObject arguments,
        CancellationToken ct)
    {
        return GuardedAsync(token => inspector.CallAsync(serverId, target.Route, tool, arguments, token), ct);
    }

    private static async Task<T> GuardedAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        try
        {
            return await run(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BuilderException(string.Format(BuilderText.ToolTestTimedOut, CallTimeout.TotalSeconds));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or McpException)
        {
            throw new BuilderException(string.Format(BuilderText.ToolTestFailed, failure.Message));
        }
    }
}
