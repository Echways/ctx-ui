using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using CtxUi.Extensions;
using MyExtension.Components;

namespace MyExtension;

public sealed class MyExtensionEntry : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        context.Panel
            .AddSection<HelloPage>(() => Text.Section)
            .In(PanelSlots.PageTop(PanelSections.Clients)).AddBlock<HelloBlock>()
            .In(PanelSlots.ClientActions).AddAction(() => Text.Greet, Greet);
    }

    private static Task Greet(PanelActionArgs<ClientSummary> args)
    {
        args.Services.GetRequiredService<ISnackbar>().Add(string.Format(Text.Greeting, args.Context.Name));
        return Task.CompletedTask;
    }
}
