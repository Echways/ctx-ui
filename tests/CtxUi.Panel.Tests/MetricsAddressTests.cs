using CtxUi.Core.Metrics;

namespace CtxUi.Panel.Tests;

public class MetricsAddressTests
{
    private static readonly Guid Client = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Empty_filter_is_the_bare_page() => Assert.Equal("metrics", MetricsAddress.Of(MetricsFilter.All));

    [Fact]
    public void Filter_and_period_go_to_the_query_with_the_tool_escaped() => Assert.Equal(
        "metrics?days=7&client=11111111-1111-1111-1111-111111111111&tool=a%20b%26c",
        MetricsAddress.Of(new MetricsFilter(ClientId: Client, Tool: "a b&c"), 7));
}
