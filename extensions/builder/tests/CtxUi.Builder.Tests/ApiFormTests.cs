using CtxUi.Builder.Components;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Servers;

namespace CtxUi.Builder.Tests;

public sealed class ApiFormTests
{
    private static ServerSettingsView Saved(ApiAuthKind kind = ApiAuthKind.Bearer, string? baseUrl = "https://api.example.com") =>
        new(Guid.NewGuid(), "weather", "weather", NetworkAccess.InternetOnly, new ServerLimits(), baseUrl, kind, null, null, kind != ApiAuthKind.None, string.Empty, 0, []);

    [Fact]
    public void A_base_url_change_alone_does_not_change_the_auth()
    {
        var form = new ApiForm { BaseUrl = "https://other.example.com/", Kind = ApiAuthKind.Bearer };

        Assert.False(form.ChangesAuth(Saved()));
        Assert.True(form.ChangesBaseUrl(Saved()));
    }

    [Fact]
    public void A_trailing_slash_is_not_a_base_url_change()
    {
        Assert.False(new ApiForm { BaseUrl = "https://api.example.com/", Kind = ApiAuthKind.Bearer }.ChangesBaseUrl(Saved()));
    }

    [Fact]
    public void A_new_secret_or_a_new_kind_changes_the_auth()
    {
        Assert.True(new ApiForm { BaseUrl = "https://api.example.com", Kind = ApiAuthKind.Bearer, Secret = "rotated" }.ChangesAuth(Saved()));
        Assert.True(new ApiForm { BaseUrl = "https://api.example.com", Kind = ApiAuthKind.None }.ChangesAuth(Saved()));
        Assert.True(new ApiForm { BaseUrl = "https://api.example.com", Kind = ApiAuthKind.Bearer }.ChangesAuth(Saved(ApiAuthKind.None)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_empty_header_name_equals_a_missing_one(string? name)
    {
        var saved = Saved(ApiAuthKind.Header) with { AuthHeaderName = null };

        Assert.False(new ApiForm { BaseUrl = "https://api.example.com", Kind = ApiAuthKind.Header, HeaderName = name }.ChangesAuth(saved));
    }

    [Fact]
    public void A_padded_header_name_equals_the_stored_one()
    {
        var saved = Saved(ApiAuthKind.Header) with { AuthHeaderName = "X-Api-Key" };

        Assert.False(new ApiForm { BaseUrl = "https://api.example.com", Kind = ApiAuthKind.Header, HeaderName = " X-Api-Key " }.ChangesAuth(saved));
    }
}
