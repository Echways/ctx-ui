using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using CtxUi.Extensions;
using SampleExtension.Components;
using SampleExtension.Components.Blocks;

namespace SampleExtension;

public sealed class SampleExtensionEntry : IPanelExtension
{
    private static readonly string[] Sections =
    [
        PanelSections.Overview, PanelSections.Metrics, PanelSections.Inbounds, PanelSections.Clients,
        PanelSections.Journal, PanelSections.OAuth, PanelSections.ApiKeys,
    ];

    public void Register(ExtensionContext context)
    {
        if (context.Configuration.GetValue<bool>("Fail"))
            throw new InvalidOperationException("Sample register failure");

        context.Panel.AddStylesheet("sample.css");
        if (context.Configuration.GetValue<bool>("Reshape"))
            Reshape(context.Panel);
        else
            context.Panel.AddSection<SamplePage>(() => SampleText.Section);

        if (context.Configuration.GetValue<bool>("Slots"))
            FillSlots(context.Panel);
        if (context.Configuration.GetValue<bool>("BoomBlock"))
            context.Panel.In(PanelSlots.PageTop(PanelSections.Clients)).AddBlock<BoomBlock>();
        if (context.Configuration.GetValue<bool>("BoomAction"))
            context.Panel.In(PanelSlots.ClientActions).AddAction(() => "Boom", _ => throw new InvalidOperationException("Sample action failure"));
    }

    private static void Reshape(PanelEditor panel) =>
        panel
            .AddSection<SamplePage>(() => SampleText.Section, PanelPosition.After(PanelSections.Overview))
            .Hide(PanelSections.Journal)
            .Rename(PanelSections.Clients, () => SampleText.Customers)
            .Customize(look =>
            {
                look.Brand = "Sample brand";
                look.Accent = "#123456";
            });

    private static void FillSlots(PanelEditor panel)
    {
        foreach (var section in Sections)
        {
            panel
                .In(PanelSlots.PageTop(section)).AddBlock<TopBlock>()
                .In(PanelSlots.PageBottom(section)).AddBlock<BottomBlock>()
                .In(PanelSlots.PageActions(section)).AddAction(() => "Page action", args => Say(args.Services, $"page:{args.Context.Section}"));
        }

        panel
            .In(PanelSlots.PageBottom(PanelSections.ApiKeys)).AddBlock<OrderTwoBlock>(order: 2)
            .In(PanelSlots.PageBottom(PanelSections.ApiKeys)).AddBlock<OrderOneBlock>(order: 1)
            .In(PanelSlots.ClientSheet).AddBlock<ClientSheetBlock>()
            .In(PanelSlots.InboundSheet).AddBlock<InboundSheetBlock>()
            .In(PanelSlots.ClientActions).AddAction(() => "Show client", args => Say(args.Services, $"client:{args.Context.Name}"))
            .In(PanelSlots.ClientActions).AddSheet<ActionSheet>(() => "Open sheet", client => $"Sheet of {client.Name}")
            .In(PanelSlots.InboundActions).AddAction(() => "Show inbound", args => Say(args.Services, $"inbound:{args.Context.Slug}"))
            .In(PanelSlots.ApiKeyActions).AddAction(() => "Show key", args => Say(args.Services, $"key:{args.Context.Name}"));
    }

    private static Task Say(IServiceProvider services, string text)
    {
        services.GetRequiredService<ISnackbar>().Add(text);
        return Task.CompletedTask;
    }
}
