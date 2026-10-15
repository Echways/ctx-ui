namespace CtxUi.Core.Clients;

public sealed record ClientLimits(
    int? RatePerMinute = null,
    int? MaxConcurrency = null,
    int? QuotaCalls = null,
    int? ResetEveryDays = null)
{
    public const int RateCeiling = 100_000;
    public const int ConcurrencyCeiling = 1_000;
    public const int QuotaCeiling = 1_000_000_000;
    public const int ResetDaysCeiling = 3_650;

    public static ClientLimits None { get; } = new();

    public static ClientLimits Of(Client client) =>
        new(client.RatePerMinute, client.MaxConcurrency, client.QuotaCalls, client.ResetEveryDays);

    public ClientLimits Validated()
    {
        Check(RatePerMinute, RateCeiling, CoreText.LimitRate);
        Check(MaxConcurrency, ConcurrencyCeiling, CoreText.LimitConcurrency);
        Check(QuotaCalls, QuotaCeiling, CoreText.LimitQuota);
        Check(ResetEveryDays, ResetDaysCeiling, CoreText.LimitResetDays);
        return this;
    }

    private static void Check(int? value, int ceiling, string what)
    {
        if (value is < 1 || value > ceiling)
            throw new DomainException(string.Format(CoreText.LimitOutOfRange, what, ceiling));
    }
}
