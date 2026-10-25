using CtxUi.Core;

namespace CtxUi.Connect;

public sealed class ConnectOptions
{
    public string PublicUrl { get; set; } = "http://localhost:8080";

    public string Language { get; set; } = Languages.Default;
}
