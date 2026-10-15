namespace CtxUi.Panel;

public sealed class PanelOptions
{
    public string PublicUrl { get; set; } = "http://localhost:8080";

    public TimeSpan SessionCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
}
