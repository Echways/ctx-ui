using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef0", "1.2.3")]
    [InlineData("1.2.3-rc.1+abcdef0", "1.2.3-rc.1")]
    [InlineData("", "")]
    public void Build_metadata_is_stripped(string version, string expected) =>
        Assert.Equal(expected, ReleaseVersion.Strip(version));

    [Fact]
    public void The_assembly_version_has_no_build_metadata() =>
        Assert.DoesNotContain('+', ReleaseVersion.Of(typeof(ReleaseVersion).Assembly));
}
