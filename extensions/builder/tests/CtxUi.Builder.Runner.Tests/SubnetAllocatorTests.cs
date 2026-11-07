namespace CtxUi.Builder.Runner.Tests;

public sealed class SubnetAllocatorTests
{
    private const string Pool = "10.213.0.0/16";

    [Fact]
    public void First_subnet_is_the_start_of_the_pool()
    {
        Assert.Equal("10.213.0.0/28", SubnetAllocator.Next(Pool, []));
    }

    [Fact]
    public void Taken_subnets_are_skipped()
    {
        Assert.Equal("10.213.0.32/28", SubnetAllocator.Next(Pool, ["10.213.0.0/28", "10.213.0.16/28"]));
    }

    [Fact]
    public void Foreign_wider_network_is_skipped_as_a_whole()
    {
        Assert.Equal("10.213.1.0/28", SubnetAllocator.Next(Pool, ["10.213.0.0/24"]));
    }

    [Fact]
    public void Network_inside_a_block_makes_the_block_taken()
    {
        Assert.Equal("10.213.0.16/28", SubnetAllocator.Next(Pool, ["10.213.0.4/30"]));
    }

    [Fact]
    public void Unrelated_networks_do_not_matter()
    {
        Assert.Equal("10.213.0.0/28", SubnetAllocator.Next(Pool, ["172.17.0.0/16", "fd00::/64"]));
    }

    [Fact]
    public void Exhausted_pool_explains_itself()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SubnetAllocator.Next("10.213.0.0/28", ["10.213.0.0/28"]));
        Assert.Contains("10.213.0.0/28", error.Message);
        Assert.Contains("no free", error.Message);
    }

    [Fact]
    public void Pool_smaller_than_a_block_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SubnetAllocator.Next("10.213.0.0/30", []));
    }
}
