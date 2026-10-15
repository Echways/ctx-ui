using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Panel;

namespace CtxUi.IntegrationTests;

public class ConnectionTextTests
{
    private const string Token = "tnk_AbCdEfGhIjKlMnOpQrStUvWxYz012345";

    private static Client Client(params string[] slugs) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Иванов 4419",
        NameKey = "ИВАНОВ 4419",
        TokenHash = "hash",
        TokenSealed = "sealed",
        TokenPrefix = "tnk_AbCdEf",
        Inbounds = slugs
            .Select(slug => new ClientInbound { Inbound = new Inbound { Slug = slug, Name = slug } })
            .ToList(),
    };

    [Fact]
    public void Addresses_start_with_the_public_url_whether_or_not_it_ends_with_a_slash()
    {
        Assert.Equal("https://mcp.example.org/mcp/learn", ConnectionText.Address("https://mcp.example.org/", "learn"));
        Assert.Equal("https://mcp.example.org/mcp/learn", ConnectionText.Address("https://mcp.example.org", "learn"));
        Assert.Equal(
            $"https://mcp.example.org/k/{Token}/mcp/learn",
            ConnectionText.AddressWithToken("https://mcp.example.org/", Token, "learn"));
    }

    [Fact]
    public void Message_for_a_client_with_a_term_and_a_renewed_quota_says_all_of_it()
    {
        var client = Client("learn", "docs");
        client.ExpiresAt = new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero);
        client.QuotaCalls = 500;
        client.ResetEveryDays = 30;

        var message = ConnectionText.Message("https://mcp.example.org/", client, Token);

        Assert.Equal(
            $"""
            Доступ к MCP-серверам для «Иванов 4419».

            Токен: {Token}

            docs
              адрес: https://mcp.example.org/mcp/docs
              заголовок: Authorization: Bearer {Token}
              если заголовок задать нельзя, адрес с токеном внутри:
              https://mcp.example.org/k/{Token}/mcp/docs

            learn
              адрес: https://mcp.example.org/mcp/learn
              заголовок: Authorization: Bearer {Token}
              если заголовок задать нельзя, адрес с токеном внутри:
              https://mcp.example.org/k/{Token}/mcp/learn

            Доступ действует до 2027-07-01 (UTC).
            Вызовов инструментов: 500 каждые 30 дн.
            Токен и адрес с токеном равносильны паролю: не передавайте их дальше.
            """.ReplaceLineEndings("\n"),
            message);
    }

    [Fact]
    public void Message_for_a_client_without_restrictions_says_so_briefly()
    {
        var message = ConnectionText.Message("https://mcp.example.org", Client("learn"), Token);

        Assert.Contains("\nДоступ бессрочный.\n", message);
        Assert.DoesNotContain("Вызовов инструментов", message);
    }

    [Fact]
    public void Quota_that_is_never_renewed_is_called_a_total()
    {
        var client = Client("learn");
        client.QuotaCalls = 100;

        Assert.Contains("\nВызовов инструментов: 100 на всё время.\n", ConnectionText.Message("https://mcp.example.org", client, Token));
    }
}
