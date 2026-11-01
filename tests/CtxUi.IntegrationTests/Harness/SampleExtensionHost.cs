using Microsoft.AspNetCore.Builder;

namespace CtxUi.IntegrationTests;

internal static class SampleExtensionHost
{
    private const string Installed = "test-extensions";

    public static Task<GatewayHost> StartAsync(
        IReadOnlyDictionary<string, string>? settings = null,
        Action<string>? prepare = null,
        Action<WebApplicationBuilder>? configure = null) =>
        GatewayHost.StartAsync(builder =>
        {
            var folder = Path.Combine(builder.Configuration["CtxUi:DataDir"]!, Installed);
            if (!Directory.Exists(folder))
            {
                CopyDirectory(Path.Combine(AppContext.BaseDirectory, "extensions", "sample"), Path.Combine(folder, "sample"));
                prepare?.Invoke(folder);
            }

            builder.Configuration["CtxUi:ExtensionsDir"] = folder;
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                builder.Configuration[key] = value;
            configure?.Invoke(builder);
        });

    public static void RewriteManifest(string extension, string from, string to)
    {
        var manifest = Path.Combine(extension, "extension.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(from, to, StringComparison.Ordinal));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
