using System.Reflection;
using System.Reflection.Emit;
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
    public void The_version_of_an_assembly_has_no_build_metadata()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Versioned"),
            AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["1.2.3-rc.1+abcdef0"])]);

        Assert.Equal("1.2.3-rc.1", ReleaseVersion.Of(assembly));
    }

    [Fact]
    public void An_assembly_without_a_version_attribute_gives_nothing() =>
        Assert.Equal("", ReleaseVersion.Of(AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Plain"), AssemblyBuilderAccess.Run)));
}
