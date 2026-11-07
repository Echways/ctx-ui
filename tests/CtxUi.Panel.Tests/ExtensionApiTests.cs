using Enfilade;
using MudBlazor;
using CtxUi.Extensions;

namespace CtxUi.Panel.Tests;

public class ExtensionApiTests
{
    private static (int Major, int Minor) MajorMinor(Type type)
    {
        var version = type.Assembly.GetName().Version!;
        return (version.Major, version.Minor);
    }

    [Fact]
    public void The_shared_ui_libraries_are_the_ones_this_api_version_promises()
    {
        Assert.Equal(new Version(2, 0), ExtensionApi.Version);
        Assert.Equal((0, 2), MajorMinor(typeof(EnfiladeBuilder)));
        Assert.Equal(9, typeof(MudButton).Assembly.GetName().Version!.Major);
    }

    [Fact]
    public void The_contract_assembly_version_follows_the_api_version_and_not_the_release()
    {
        var version = typeof(ExtensionApi).Assembly.GetName().Version!;

        Assert.Equal(ExtensionApi.Version, new Version(version.Major, version.Minor));
        Assert.Equal((0, 0), (version.Build, version.Revision));
    }

    [Fact]
    public void Summaries_carry_no_secrets()
    {
        string[] forbidden = ["Token", "Hash", "Sealed", "Secret", "Headers", "Url"];
        var names = new[] { typeof(ClientSummary), typeof(InboundSummary), typeof(ApiKeySummary) }
            .SelectMany(type => type.GetProperties()).Select(property => property.Name);
        Assert.DoesNotContain(names,
            name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
