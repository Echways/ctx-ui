using CtxUi.Connect.SignIn;

namespace CtxUi.Connect.Tests;

public class PhrasePatternTests
{
    [Fact]
    public void Placeholders_alternate_with_text() =>
        Assert.Equal(["Sign in to connect ", "0", " to ", "1", "."], PhrasePattern.Split("Sign in to connect {0} to {1}."));

    [Fact]
    public void A_pattern_without_placeholders_stays_whole() => Assert.Equal(["Done"], PhrasePattern.Split("Done"));
}
