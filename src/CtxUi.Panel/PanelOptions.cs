using CtxUi.Core;

namespace CtxUi.Panel;

public sealed class PanelOptions
{
    public string PublicUrl { get; set; } = "http://localhost:8080";

    public string Language { get; set; } = Languages.Default;

    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
