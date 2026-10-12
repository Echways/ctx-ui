namespace CtxUi.Server;

public sealed class CtxUiOptions
{
    public const string Section = "CtxUi";

    public string DataDir { get; set; } = "data";

    public string PublicUrl { get; set; } = "http://localhost:8080";
}
