namespace CtxUi.Core.Metrics;

public sealed record MetricsFilter(Guid? ClientId = null, Guid? InboundId = null, string? Tool = null)
{
    public static readonly MetricsFilter All = new();
}
