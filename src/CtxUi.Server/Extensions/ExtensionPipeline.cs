using System.Runtime.Loader;
using Microsoft.Extensions.FileProviders;
using CtxUi.Extensions;
using CtxUi.Server.Api;

namespace CtxUi.Server.Extensions;

public static class ExtensionPipeline
{
    private const string Assets = "wwwroot";

    public static void Register(PanelLayout layout, IServiceCollection services, ExtensionCatalog catalog, IConfiguration section)
    {
        foreach (var extension in catalog.Loaded)
        {
            var manifest = extension.Manifest;
            ServiceRegistration.CreatePrivateDirectory(extension.DataDirectory);
            var editor = layout.EditorFor(manifest.Assembly);
            try
            {
                extension.Entry.Register(new ExtensionContext(
                    manifest.Id,
                    editor,
                    services,
                    section.GetSection($"Extensions:{manifest.Id}"),
                    extension.DataDirectory));
            }
            catch (Exception failure)
            {
                throw Failed(manifest.Id, failure);
            }

            if (File.Exists(Path.Combine(extension.Directory, Assets, $"{manifest.Assembly}.styles.css")))
                editor.AddStylesheet($"{manifest.Assembly}.styles.css");
        }
    }

    public static IApplicationBuilder UseExtensionAssets(this IApplicationBuilder app, ExtensionCatalog catalog)
    {
        foreach (var extension in catalog.Loaded)
        {
            var assets = Path.Combine(extension.Directory, Assets);
            if (Directory.Exists(assets))
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(assets),
                    RequestPath = $"/_content/{extension.Manifest.Assembly}",
                });
        }

        return app;
    }

    public static WebApplication MapExtensionEndpoints(this WebApplication app, ExtensionCatalog catalog)
    {
        foreach (var extension in catalog.Loaded)
        {
            var group = app.MapGroup($"/extensions/{extension.Manifest.Id}").RequireAuthorization(ApiServices.ExtensionsPolicy);
            try
            {
                extension.Entry.MapEndpoints(group);
            }
            catch (Exception failure)
            {
                throw Failed(extension.Manifest.Id, failure);
            }
        }

        return app;
    }

    public static async Task InitializeExtensionsAsync(IServiceProvider services, CancellationToken ct)
    {
        int count;
        await using (var counting = services.CreateAsyncScope())
            count = counting.ServiceProvider.GetServices<IExtensionInitializer>().Count();

        for (var index = 0; index < count; index++)
        {
            await using var scope = services.CreateAsyncScope();
            var initializer = scope.ServiceProvider.GetServices<IExtensionInitializer>().ElementAt(index);
            try
            {
                await initializer.InitializeAsync(ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                throw Failed(OwnerOf(initializer), failure);
            }
        }
    }

    private static string OwnerOf(IExtensionInitializer initializer)
    {
        var type = initializer.GetType();
        return type.GetGenericArguments().Prepend(type)
            .Select(candidate => AssemblyLoadContext.GetLoadContext(candidate.Assembly))
            .OfType<ExtensionLoadContext>()
            .FirstOrDefault()?.Name ?? type.FullName!;
    }

    internal static InvalidOperationException Failed(string id, Exception failure) =>
        new(string.Format(ServerText.ExtensionStartFailed, id, failure.Message), failure);
}
