using CtxUi.Extensions;

namespace CtxUi.Server.Extensions;

public sealed record LoadedExtension(ExtensionManifest Manifest, string Directory, string DataDirectory, IPanelExtension Entry);

public sealed record RejectedExtension(string Name, string Reason);

public sealed class ExtensionCatalog
{
    public const string Disabled = "disabled";

    public const string DataFolder = "extension-data";

    private const string DefaultFolder = "extensions";

    private ExtensionCatalog(IReadOnlyList<LoadedExtension> loaded, IReadOnlyList<RejectedExtension> rejected)
    {
        Loaded = loaded;
        Rejected = rejected;
    }

    public IReadOnlyList<LoadedExtension> Loaded { get; }

    public IReadOnlyList<RejectedExtension> Rejected { get; }

    public static string DirectoryOf(CtxUiOptions options) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(options.ExtensionsDir) ? Path.Combine(options.DataDir, DefaultFolder) : options.ExtensionsDir);

    public static IReadOnlySet<string> DisabledOf(CtxUiOptions options) =>
        options.DisabledExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    public static ExtensionCatalog Load(CtxUiOptions options)
    {
        List<LoadedExtension> loaded = [];
        List<RejectedExtension> rejected = [];
        var root = DirectoryOf(options);
        var disabled = DisabledOf(options);
        var folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        foreach (var folder in folders.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.'))
                continue;

            try
            {
                var extension = LoadOne(folder, name, disabled, options);
                if (extension is null)
                    rejected.Add(new RejectedExtension(name, Disabled));
                else
                    loaded.Add(extension);
            }
            catch (Exception failure)
            {
                rejected.Add(new RejectedExtension(name, failure.Message));
            }
        }

        return new ExtensionCatalog(loaded, rejected);
    }

    private static LoadedExtension? LoadOne(string folder, string name, IReadOnlySet<string> disabled, CtxUiOptions options)
    {
        var manifest = ExtensionManifest.Read(folder);
        Check(ExtensionRules.IsValidId(manifest.Id), string.Format(ServerText.ExtensionInvalidId, manifest.Id));
        Check(manifest.Id == name, string.Format(ServerText.ExtensionFolderMismatch, name, manifest.Id));
        if (disabled.Contains(manifest.Id))
            return null;

        Check(!ExtensionRules.IsReservedId(manifest.Id), string.Format(ServerText.ExtensionReservedId, manifest.Id));
        Check(ExtensionRules.IsCompatible(manifest.Api), string.Format(ServerText.ExtensionIncompatible, manifest.Api, ExtensionApi.Version.ToString(2)));

        var path = Path.Combine(folder, manifest.Assembly + ".dll");
        var assembly = new ExtensionLoadContext(manifest.Id, path).LoadFromAssemblyPath(path);
        var entries = assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPanelExtension).IsAssignableFrom(type)).ToList();
        Check(entries.Count == 1, string.Format(ServerText.ExtensionEntryCount, entries.Count));
        var violation = ExtensionRules.RouteViolation(manifest.Id, assembly);
        Check(violation is null, violation ?? "");

        var entry = (IPanelExtension)Activator.CreateInstance(entries[0])!;
        return new LoadedExtension(manifest, folder, Path.Combine(Path.GetFullPath(options.DataDir), DataFolder, manifest.Id), entry);
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException(reason);
    }
}
