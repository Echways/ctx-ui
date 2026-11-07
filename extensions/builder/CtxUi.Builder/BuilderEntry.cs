using CtxUi.Builder.Components;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using CtxUi.Extensions;
using Enfilade;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace CtxUi.Builder;

public sealed class BuilderEntry : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        var settings = new RunnerSettings(
            context.Configuration["RunnerUrl"],
            context.Configuration["RunnerIngressUrl"],
            context.Configuration["RunnerToken"]);

        var connected = new ConnectedInbounds();

        context.AddDatabase<BuilderDatabase>();
        context.Services.AddSingleton(settings);
        context.Services.AddHttpClient<RunnerClient>(client =>
        {
            client.BaseAddress = settings.Url;
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (settings.Token is not null)
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderNames.Authorization,
                    $"Bearer {settings.Token}");
        });
        context.Services.AddSingleton<SecretBox>();
        context.Services.AddSingleton<ServerCatalog>();
        context.Services.AddScoped<PendingImports>();
        context.Services.AddSingleton(connected);
        context.Services.AddSingleton<IExtensionInitializer, ConnectedInboundsLoader>();
        context.Services.AddScoped<ConnectionService>();
        context.Services.AddScoped<RunnerAvailability>();
        context.Services.AddPublishing(context.DataDirectory);
        context.Services.ShowMessageOf<BuilderException>(failure => failure.Message);

        context.Panel
            .AddCaption(() => BuilderText.Caption)
            .AddSection<ServersPage>(() => BuilderText.Servers)
            .In(PanelSlots.InboundActions).When(inbound => connected.Contains(inbound.Id))
            .AddAction(() => BuilderText.OpenInBuilder, OpenInBuilderAsync)
            .In(PanelSlots.InboundSheet).AddBlock<InboundBuilderBlock>()
            .AddStylesheet("builder.css");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        BuilderEndpoints.Map(endpoints);
    }

    private static async Task OpenInBuilderAsync(PanelActionArgs<InboundSummary> args)
    {
        var connections = args.Services.GetRequiredService<ConnectionService>();
        var serverId = await connections.FindServerIdAsync(args.Context, CancellationToken.None)
                       ?? throw new BuilderException(BuilderText.InboundNotBuilt);
        args.Services.GetRequiredService<NavigationManager>().NavigateTo($"builder/{serverId}");
    }
}
