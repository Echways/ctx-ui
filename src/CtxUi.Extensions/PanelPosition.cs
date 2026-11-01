namespace CtxUi.Extensions;

public sealed class PanelPosition
{
    private PanelPosition(string entry, bool isAfter)
    {
        Entry = entry;
        IsAfter = isAfter;
    }

    internal string Entry { get; }

    internal bool IsAfter { get; }

    public static PanelPosition Before(string entry) => new(entry, isAfter: false);

    public static PanelPosition After(string entry) => new(entry, isAfter: true);
}
