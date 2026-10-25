namespace CtxUi.Core.Routing;

public interface ISnapshotSource
{
    RoutingSnapshot Current { get; }

    Task RefreshAsync(CancellationToken ct = default);
}
