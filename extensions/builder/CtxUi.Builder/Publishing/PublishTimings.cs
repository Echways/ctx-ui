namespace CtxUi.Builder.Publishing;

public sealed record PublishTimings(TimeSpan PollInterval, TimeSpan BuildTimeout, TimeSpan StartTimeout, TimeSpan CheckTimeout, TimeSpan OutageTolerance)
{
    public static PublishTimings Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(12),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60));
}
