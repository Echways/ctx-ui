namespace CtxUi.Builder.Runner.Tests;

public sealed class RunnerOptionsTests
{
    private static Dictionary<string, string?> Environment() => new()
    {
        ["RUNNER_TOKEN"] = "token",
        ["RUNNER_BIND"] = "10.212.0.2",
        ["RUNNER_RUNTIME_IMAGE"] = "runtime:1",
        ["RUNNER_EGRESS_CONTAINER"] = "egress",
        ["HOSTNAME"] = "abcdef"
    };

    [Fact]
    public void Values_come_from_the_environment_with_the_default_pool()
    {
        var options = RunnerOptions.FromEnvironment(Environment());

        Assert.Equal("token", options.Token);
        Assert.Equal("10.212.0.2", options.BindAddress);
        Assert.Equal("runtime:1", options.RuntimeImage);
        Assert.Equal("egress", options.EgressContainer);
        Assert.Equal("10.213.0.0/16", options.SubnetPool);
        Assert.Null(options.ContainerRuntime);
        Assert.Equal("abcdef", options.SelfContainer);
    }

    [Fact]
    public void Container_runtime_and_pool_are_read_when_set()
    {
        var variables = Environment();
        variables["BUILDER_RUNTIME"] = "runsc";
        variables["RUNNER_SUBNET_POOL"] = "10.50.0.0/16";

        var options = RunnerOptions.FromEnvironment(variables);

        Assert.Equal("runsc", options.ContainerRuntime);
        Assert.Equal("10.50.0.0/16", options.SubnetPool);
    }

    [Theory]
    [InlineData("RUNNER_TOKEN")]
    [InlineData("RUNNER_BIND")]
    [InlineData("RUNNER_RUNTIME_IMAGE")]
    [InlineData("RUNNER_EGRESS_CONTAINER")]
    [InlineData("HOSTNAME")]
    public void A_missing_required_value_is_named(string name)
    {
        var variables = Environment();
        variables.Remove(name);

        var failure = Assert.Throws<InvalidOperationException>(() => RunnerOptions.FromEnvironment(variables));

        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", failure.Message.Replace("RUNNER_TOKEN", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("not-an-address")]
    public void Wildcard_or_invalid_bind_address_is_refused(string address)
    {
        var variables = Environment();
        variables["RUNNER_BIND"] = address;

        var failure = Assert.Throws<InvalidOperationException>(() => RunnerOptions.FromEnvironment(variables));

        Assert.Contains("RUNNER_BIND", failure.Message, StringComparison.Ordinal);
    }
}
