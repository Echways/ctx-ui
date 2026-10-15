using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class ClientStateTests
{
    private static Client Client(Action<Client>? change = null)
    {
        var client = TestData.Client("Иванов");
        change?.Invoke(client);
        return client;
    }

    private static void Spend(Client client, long calls)
    {
        client.UsedCalls = calls;
        client.UsedSince = client.PeriodStartedAt;
    }

    [Fact]
    public void Client_without_restrictions_is_active() =>
        Assert.Equal(ClientStatus.Active, ClientState.Of(Client(), TestData.Now));

    [Fact]
    public void Switched_off_client_is_disabled_whatever_else_is_true()
    {
        var client = Client(client =>
        {
            client.Enabled = false;
            client.ExpiresAt = TestData.Now.AddDays(-1);
        });

        Assert.Equal(ClientStatus.Disabled, ClientState.Of(client, TestData.Now));
    }

    [Fact]
    public void Client_is_expired_from_the_very_moment_of_expiry()
    {
        var client = Client(client => client.ExpiresAt = TestData.Now);

        Assert.Equal(ClientStatus.Expired, ClientState.Of(client, TestData.Now));
        Assert.Equal(ClientStatus.Active, ClientState.Of(client, TestData.Now.AddMilliseconds(-1)));
    }

    [Fact]
    public void Client_that_spent_the_whole_quota_is_exhausted()
    {
        var client = Client(client => client.QuotaCalls = 2);
        Spend(client, 2);

        Assert.Equal(ClientStatus.Exhausted, ClientState.Of(client, TestData.Now));
    }

    [Fact]
    public void Calls_left_or_no_quota_at_all_keep_the_client_active()
    {
        var limited = Client(client => client.QuotaCalls = 2);
        Spend(limited, 1);
        var unlimited = Client();
        Spend(unlimited, 1_000_000);

        Assert.Equal(ClientStatus.Active, ClientState.Of(limited, TestData.Now));
        Assert.Equal(ClientStatus.Active, ClientState.Of(unlimited, TestData.Now));
    }

    [Fact]
    public void Counter_of_a_finished_period_does_not_count()
    {
        var client = Client(client =>
        {
            client.QuotaCalls = 2;
            client.ResetEveryDays = 30;
        });
        Spend(client, 2);

        Assert.Equal(ClientStatus.Exhausted, ClientState.Of(client, TestData.Now.AddDays(29)));
        Assert.Equal(ClientStatus.Active, ClientState.Of(client, TestData.Now.AddDays(30)));
    }

    [Theory]
    [InlineData(ClientStatus.Active, "активен")]
    [InlineData(ClientStatus.Disabled, "отключён")]
    [InlineData(ClientStatus.Expired, "истёк")]
    [InlineData(ClientStatus.Exhausted, "исчерпан")]
    public void Every_state_has_a_word(ClientStatus status, string word) => Assert.Equal(word, ClientState.Text(status));
}
