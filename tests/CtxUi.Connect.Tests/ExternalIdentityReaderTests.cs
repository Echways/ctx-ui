using System.Security.Claims;
using CtxUi.Connect.External;

namespace CtxUi.Connect.Tests;

public class ExternalIdentityReaderTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Email_is_shown_but_subject_identifies()
    {
        var identity = ExternalIdentityReader.Read(
            "github",
            Principal(new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Name, "ivanov"), new Claim(ClaimTypes.Email, "ivanov@example.org")));

        Assert.NotNull(identity);
        Assert.Equal("github", identity.Provider);
        Assert.Equal("42", identity.Subject);
        Assert.Equal("ivanov@example.org", identity.Display);
    }

    [Fact]
    public void Name_is_shown_without_email_and_subject_without_both()
    {
        Assert.Equal("ivanov", ExternalIdentityReader.Read("github", Principal(new Claim("sub", "42"), new Claim(ClaimTypes.Name, "ivanov")))!.Display);
        Assert.Equal("42", ExternalIdentityReader.Read("github", Principal(new Claim("sub", "42")))!.Display);
    }

    [Fact]
    public void Missing_subject_is_null() =>
        Assert.Null(ExternalIdentityReader.Read("github", Principal(new Claim(ClaimTypes.Email, "ivanov@example.org"))));
}
