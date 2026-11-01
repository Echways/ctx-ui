namespace CtxUi.Extensions;

public sealed record PanelSlot<TContext>(string Name);

public sealed record PanelActionSlot<TContext>(string Name);

public sealed record PanelPage(string Section);

public static class PanelSlots
{
    public static readonly PanelActionSlot<ClientSummary> ClientActions = new("client-actions");
    public static readonly PanelSlot<ClientSummary> ClientSheet = new("client-sheet");
    public static readonly PanelActionSlot<InboundSummary> InboundActions = new("inbound-actions");
    public static readonly PanelSlot<InboundSummary> InboundSheet = new("inbound-sheet");
    public static readonly PanelActionSlot<ApiKeySummary> ApiKeyActions = new("api-key-actions");

    public static PanelSlot<PanelPage> PageTop(string section) => new($"page-top:{section}");

    public static PanelSlot<PanelPage> PageBottom(string section) => new($"page-bottom:{section}");

    public static PanelActionSlot<PanelPage> PageActions(string section) => new($"page-actions:{section}");
}
