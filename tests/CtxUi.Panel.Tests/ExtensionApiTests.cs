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
        Assert.Equal(new Version(1, 0), ExtensionApi.Version);
        Assert.Equal((0, 1), MajorMinor(typeof(EnfiladeBuilder)));
        Assert.Equal(9, typeof(MudButton).Assembly.GetName().Version!.Major);
    }
}
