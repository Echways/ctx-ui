using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using CtxUi.Extensions;
using SampleExtension.Components;
using SampleExtension.Components.Blocks;
using SampleExtension.Data;

namespace SampleExtension;

public sealed class SampleExtensionEntry : IPanelExtension
{
    private static readonly string[] Sections =
    [
        PanelSections.Overview, PanelSections.Metrics, PanelSections.Inbounds, PanelSections.Clients,
        PanelSections.Journal, PanelSections.OAuth, PanelSections.ApiKeys
    ];

    private bool _database;
    private bool _hosted;

    public void Register(ExtensionContext context)
    {
        if (context.Configuration.GetValue<bool>("Fail"))
            throw new InvalidOperationException("Sample register failure");

        _database = context.Configuration.GetValue<bool>("Database");
        _hosted = context.Configuration.GetValue<bool>("Hosted");
        if (_database)
            context.AddDatabase<SampleDatabase>();
        if (_hosted)
            context.Services.AddSingleton<SampleStatus>().AddHostedService<SampleHostedService>();
        if (context.Configuration.GetValue<bool>("FailInitializer"))
            context.Services.AddScoped<IExtensionInitializer, FailingInitializer>();

        context.Panel.AddStylesheet("sample.css");
        if (context.Configuration.GetValue<bool>("Reshape"))
            Reshape(context.Panel);
        else if (!context.Configuration.GetValue<bool>("Headless"))
            context.Panel.AddSection<SamplePage>(() => SampleText.Section);

        if (context.Configuration.GetValue<bool>("Slots"))
            FillSlots(context.Panel);
        if (context.Configuration.GetValue<bool>("BoomBlock"))
            context.Panel.In(PanelSlots.PageTop(PanelSections.Clients)).AddBlock<BoomBlock>();
        if (context.Configuration.GetValue<bool>("BoomAction"))
            context.Panel.In(PanelSlots.ClientActions).AddAction(() => "Boom",
                _ => throw new InvalidOperationException("Sample action failure"));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ping", () => "pong");
        if (_hosted)
            endpoints.MapGet("/hosted", (SampleStatus status) => status.Value);
        if (_database)
        {
            endpoints.MapPost("/notes", async ([FromBody] string text, SampleDatabase database) =>
            {
                database.Notes.Add(new SampleNote { Text = text });
                await database.SaveChangesAsync();
            });
            endpoints.MapGet("/notes", async (SampleDatabase database) => (await database.Notes.SingleAsync()).Text);
        }
    }

    private static void Reshape(PanelEditor panel)
    {
        panel
            .AddSection<SamplePage>(() => SampleText.Section, PanelPosition.After(PanelSections.Overview))
            .Hide(PanelSections.Journal)
            .Rename(PanelSections.Clients, () => SampleText.Customers)
            .Customize(look =>
            {
                look.Brand = "Sample brand";
                look.Accent = "#123456";
            });
    }

    private static void FillSlots(PanelEditor panel)
    {
        foreach (var section in Sections)
            panel
                .In(PanelSlots.PageTop(section)).AddBlock<TopBlock>()
                .In(PanelSlots.PageBottom(section)).AddBlock<BottomBlock>()
                .In(PanelSlots.PageActions(section)).AddAction(() => "Page action",
                    args => Say(args.Services, $"page:{args.Context.Section}"));

        panel
            .In(PanelSlots.PageBottom(PanelSections.ApiKeys)).AddBlock<OrderTwoBlock>(2)
            .In(PanelSlots.PageBottom(PanelSections.ApiKeys)).AddBlock<OrderOneBlock>(1)
            .In(PanelSlots.ClientSheet).AddBlock<ClientSheetBlock>()
            .In(PanelSlots.InboundSheet).AddBlock<InboundSheetBlock>()
            .In(PanelSlots.ClientActions).AddAction(() => "Show client",
                args => Say(args.Services, $"client:{args.Context.Name}"))
            .In(PanelSlots.ClientActions).AddSheet<ActionSheet>(() => "Open sheet", client => $"Sheet of {client.Name}")
            .In(PanelSlots.ClientActions)
            .AddSheet<BoomSheet>(() => "Open broken sheet", client => $"Broken sheet of {client.Name}")
            .In(PanelSlots.ClientActions).AddAction(() => "Turn off",
                args => args.Services.GetRequiredService<IPanelData>().SetClientEnabledAsync(args.Context.Id, false))
            .In(PanelSlots.InboundActions).AddAction(() => "Show inbound",
                args => Say(args.Services, $"inbound:{args.Context.Slug}"))
            .In(PanelSlots.ApiKeyActions)
            .AddAction(() => "Show key", args => Say(args.Services, $"key:{args.Context.Name}"));
    }

    private static Task Say(IServiceProvider services, string text)
    {
        services.GetRequiredService<ISnackbar>().Add(text);
        return Task.CompletedTask;
    }
}
