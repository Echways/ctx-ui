using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class ClientLimitsTests
{
    [Fact]
    public void Empty_limits_mean_no_restriction_and_are_valid()
    {
        var limits = ClientLimits.None.Validated();

        Assert.Null(limits.RatePerMinute);
        Assert.Null(limits.MaxConcurrency);
        Assert.Null(limits.QuotaCalls);
        Assert.Null(limits.ResetEveryDays);
    }

    [Fact]
    public void Smallest_and_largest_values_are_accepted()
    {
        new ClientLimits(1, 1, 1, 1).Validated();
        new ClientLimits(
            ClientLimits.RateCeiling,
            ClientLimits.ConcurrencyCeiling,
            ClientLimits.QuotaCeiling,
            ClientLimits.ResetDaysCeiling).Validated();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void Value_outside_the_range_is_rejected_for_every_limit(int value)
    {
        var rate = Assert.Throws<DomainException>(() => new ClientLimits(RatePerMinute: value).Validated());
        var concurrency = Assert.Throws<DomainException>(() => new ClientLimits(MaxConcurrency: value).Validated());
        var quota = Assert.Throws<DomainException>(() => new ClientLimits(QuotaCalls: value).Validated());
        var reset = Assert.Throws<DomainException>(() => new ClientLimits(ResetEveryDays: value).Validated());

        Assert.Contains("запросов в минуту", rate.Message);
        Assert.Contains("одновременных", concurrency.Message);
        Assert.Contains("квота", quota.Message);
        Assert.Contains("период сброса", reset.Message);
    }

    [Fact]
    public void Limits_are_read_from_the_client()
    {
        var client = TestData.Client("Иванов");
        client.RatePerMinute = 60;
        client.MaxConcurrency = 2;
        client.QuotaCalls = 100;
        client.ResetEveryDays = 30;

        Assert.Equal(new ClientLimits(60, 2, 100, 30), ClientLimits.Of(client));
    }
}
