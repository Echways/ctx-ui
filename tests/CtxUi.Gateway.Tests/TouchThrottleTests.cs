namespace CtxUi.Gateway.Tests;

public class TouchThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly TouchThrottle _throttle = new(TimeSpan.FromMinutes(1));
    private readonly Guid _client = Guid.NewGuid();

    [Fact]
    public void First_request_of_a_client_is_recorded() => Assert.True(_throttle.ShouldTouch(_client, Start));

    [Fact]
    public void Requests_inside_the_interval_are_skipped()
    {
        _throttle.ShouldTouch(_client, Start);

        Assert.False(_throttle.ShouldTouch(_client, Start.AddSeconds(59)));
    }

    [Fact]
    public void Request_after_the_interval_is_recorded_and_restarts_it()
    {
        _throttle.ShouldTouch(_client, Start);

        Assert.True(_throttle.ShouldTouch(_client, Start.AddSeconds(60)));
        Assert.False(_throttle.ShouldTouch(_client, Start.AddSeconds(90)));
    }

    [Fact]
    public void Clients_are_throttled_independently()
    {
        _throttle.ShouldTouch(_client, Start);

        Assert.True(_throttle.ShouldTouch(Guid.NewGuid(), Start));
    }
}
