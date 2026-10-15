namespace CtxUi.Core.Routing;

public interface IRoutingRefresh
{
    Task RefreshAsync(CancellationToken ct = default);
}
