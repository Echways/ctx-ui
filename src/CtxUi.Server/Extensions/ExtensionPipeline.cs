using Microsoft.Extensions.FileProviders;
using CtxUi.Extensions;

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

    internal static InvalidOperationException Failed(string id, Exception failure) =>
        new(string.Format(ServerText.ExtensionStartFailed, id, failure.Message), failure);
}
