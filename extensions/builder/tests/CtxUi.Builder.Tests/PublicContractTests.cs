namespace CtxUi.Builder.Tests;

public sealed class PublicContractTests
{
    [Fact]
    public void Extension_depends_only_on_the_public_contract()
    {
        var names = typeof(BuilderEntry).Assembly.GetReferencedAssemblies().Select(name => name.Name);
        Assert.DoesNotContain(names,
            name => name is "CtxUi.Core" or "CtxUi.Panel" or "CtxUi.Gateway" or "CtxUi.Server");
    }

    [Fact]
    public void Extension_exposes_no_internals_to_other_assemblies()
    {
        var friends = typeof(BuilderEntry).Assembly.GetCustomAttributesData()
            .Where(data => data.AttributeType.Name == "InternalsVisibleToAttribute");
        Assert.Empty(friends);
    }
}
