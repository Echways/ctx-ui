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

    public static PanelPosition Before(string entry)
    {
        return new PanelPosition(entry, false);
    }

    public static PanelPosition After(string entry)
    {
        return new PanelPosition(entry, true);
    }
}
