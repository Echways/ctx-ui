using CtxUi.Core;
using CtxUi.Core.Settings;

namespace CtxUi.Server;

public sealed class CtxUiOptions
{
    public const string Section = "CtxUi";

    public string DataDir { get; set; } = "data";

    public string PublicUrl { get; set; } = PublicEndpointOptions.DefaultUrl;

    public string Language { get; set; } = Languages.Default;

    public string TrustedProxies { get; set; } = "";

    public string? ExtensionsDir { get; set; }

    public string DisabledExtensions { get; set; } = "";
}
