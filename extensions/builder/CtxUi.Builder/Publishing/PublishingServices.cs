using CtxUi.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CtxUi.Builder.Publishing;

public static class PublishingServices
{
    public static IServiceCollection AddPublishing(this IServiceCollection services, string dataDirectory)
    {
        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(PublishTimings.Default);
        services.AddSingleton(new ReleaseArchives(dataDirectory));
        services.AddSingleton<IToolInspector, ToolInspector>();
        services.AddSingleton<PublishPipeline>();
        services.AddSingleton<PublishQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<PublishQueue>());
        services.AddSingleton<IExtensionInitializer>(provider => provider.GetRequiredService<PublishQueue>());
        services.AddScoped<ReleaseCatalog>();
        services.AddSingleton<ReleaseReader>();
        return services;
    }
}
