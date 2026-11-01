namespace CtxUi.Extensions;

public sealed class PanelSlot<TContext>
{
    internal PanelSlot(string name) => Name = name;

    internal string Name { get; }
}

public sealed class PanelActionSlot<TContext>
{
    internal PanelActionSlot(string name) => Name = name;

    internal string Name { get; }
}

public sealed record PanelPage(string Section);

public static class PanelSlots
{
    public static readonly PanelActionSlot<ClientSummary> ClientActions = new("client-actions");
    public static readonly PanelSlot<ClientSummary> ClientSheet = new("client-sheet");
    public static readonly PanelActionSlot<InboundSummary> InboundActions = new("inbound-actions");
    public static readonly PanelSlot<InboundSummary> InboundSheet = new("inbound-sheet");
    public static readonly PanelActionSlot<ApiKeySummary> ApiKeyActions = new("api-key-actions");

    public static PanelSlot<PanelPage> PageTop(string section) => new(PageSlot("page-top", section));

    public static PanelSlot<PanelPage> PageBottom(string section) => new(PageSlot("page-bottom", section));

    public static PanelActionSlot<PanelPage> PageActions(string section) => new(PageSlot("page-actions", section));

    private static string PageSlot(string place, string section) =>
        PanelSections.IsBuiltInPage(section)
            ? $"{place}:{section}"
            : throw new ArgumentException($"'{section}' is not a built-in panel page, so it has no slots", nameof(section));
}
