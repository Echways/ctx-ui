using CtxUi.Builder.Components;
using CtxUi.Builder.Data;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using CtxUi.Extensions;
using Microsoft.Extensions.Configuration;
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

        context.AddDatabase<BuilderDatabase>();
        context.Services.AddSingleton(settings);
        context.Services.AddHttpClient<RunnerClient>(client =>
        {
            client.BaseAddress = settings.Url;
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (settings.Token is not null)
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderNames.Authorization, $"Bearer {settings.Token}");
        });
        context.Services.AddSingleton<SecretBox>();
        context.Services.AddScoped<ServerCatalog>();
        context.Services.AddScoped<RunnerAvailability>();

        context.Panel
            .AddCaption(() => BuilderText.Caption)
            .AddSection<ServersPage>(() => BuilderText.Servers)
            .AddStylesheet("builder.css");
    }
}
