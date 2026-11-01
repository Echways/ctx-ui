namespace CtxUi.Extensions;

public sealed record PanelPosition(string? BeforeEntry, string? AfterEntry)
{
    public static PanelPosition Before(string entry) => new(entry, null);

    public static PanelPosition After(string entry) => new(null, entry);
}
