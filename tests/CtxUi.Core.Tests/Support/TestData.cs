using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    public static Inbound Inbound(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = slug,
        Kind = InboundKind.Http,
        Url = "http://upstream.test/mcp",
        CreatedAt = Now,
    };

    public static Client Client(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        NameKey = ClientName.Key(name),
        TokenHash = AccessToken.Hash(name),
        TokenSealed = "sealed",
        TokenPrefix = "tnk_test00",
        CreatedAt = Now,
    };
}
