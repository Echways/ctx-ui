using System.Text.Json.Nodes;
using Enfilade;
using ModelContextProtocol;

namespace CtxUi.Builder.Publishing;

public sealed record ToolTarget(int Number, int? Route, bool IsCandidate);

public sealed class ToolTestRunner(IToolInspector inspector, ReleaseCatalog releases)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public Task<ToolTarget?> TargetAsync(Guid serverId, CancellationToken ct) => releases.TestTargetAsync(serverId, ct);

    public Task<IReadOnlyList<ToolInfo>> ListAsync(Guid serverId, ToolTarget target, CancellationToken ct) =>
        GuardedAsync(token => inspector.ListAsync(serverId, target.Route, token), ct);

    public Task<ToolCallOutcome> CallAsync(Guid serverId, ToolTarget target, string tool, JsonObject arguments, CancellationToken ct) =>
        GuardedAsync(token => inspector.CallAsync(serverId, target.Route, tool, arguments, token), ct);

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
            throw new PanelException(string.Format(BuilderText.ToolTestTimedOut, CallTimeout.TotalSeconds));
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or McpException)
        {
            throw new PanelException(string.Format(BuilderText.ToolTestFailed, failure.Message));
        }
    }
}
