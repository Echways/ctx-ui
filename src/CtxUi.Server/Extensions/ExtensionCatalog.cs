using System.Reflection;
using CtxUi.Extensions;

namespace CtxUi.Server.Extensions;

public enum ExtensionState
{
    Ok,
    Disabled,
    Incompatible,
    Invalid,
}

public sealed record ExtensionStatus(string Folder, ExtensionManifest? Manifest, ExtensionState State, string? Problem = null, Type? Entry = null)
{
    public string Name => Path.GetFileName(Folder);
}

public sealed record LoadedExtension(ExtensionManifest Manifest, string Directory, string DataDirectory, IPanelExtension Entry);

public sealed record RejectedExtension(string Name, string Reason);

public sealed class ExtensionCatalog
{
    public const string DataFolder = "extension-data";

    public const string DefaultFolder = "extensions";

    private ExtensionCatalog(IReadOnlyList<LoadedExtension> loaded, IReadOnlyList<string> disabled, IReadOnlyList<RejectedExtension> rejected)
    {
        Loaded = loaded;
        Disabled = disabled;
        Rejected = rejected;
    }

    public IReadOnlyList<LoadedExtension> Loaded { get; }

    public IReadOnlyList<string> Disabled { get; }

    public IReadOnlyList<RejectedExtension> Rejected { get; }

    public static string DirectoryOf(CtxUiOptions options) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(options.ExtensionsDir) ? Path.Combine(options.DataDir, DefaultFolder) : options.ExtensionsDir);

    public static IReadOnlyList<ExtensionStatus> Scan(CtxUiOptions options)
    {
        var root = DirectoryOf(options);
        if (!Directory.Exists(root))
            return [];

        var disabled = options.DisabledExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. Directory.GetDirectories(root)
                .Where(folder => !Path.GetFileName(folder).StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .Select(folder => Inspect(folder, Path.GetFileName(folder), disabled)),
        ];
    }

    public static ExtensionStatus Inspect(string folder, string? expectedId = null, IReadOnlySet<string>? disabled = null)
    {
        ExtensionManifest? manifest = null;
        try
        {
            manifest = ExtensionManifest.Read(folder);
            Check(ExtensionRules.IsValidId(manifest.Id), string.Format(ServerText.ExtensionInvalidId, manifest.Id));
            Check(expectedId is null || manifest.Id == expectedId, string.Format(ServerText.ExtensionFolderMismatch, expectedId, manifest.Id));
            if (disabled?.Contains(manifest.Id) == true)
                return new ExtensionStatus(folder, manifest, ExtensionState.Disabled);

            Check(!ExtensionRules.IsReservedId(manifest.Id), string.Format(ServerText.ExtensionReservedId, manifest.Id));
            if (!ExtensionRules.IsCompatible(manifest.Api))
                return new ExtensionStatus(folder, manifest, ExtensionState.Incompatible, string.Format(ServerText.ExtensionIncompatible, manifest.Api, ExtensionApi.Version.ToString(2)));

            var path = Path.Combine(folder, manifest.Assembly + ".dll");
            Check(File.Exists(path), string.Format(ServerText.ExtensionAssemblyMissing, manifest.Assembly));
            var assembly = new ExtensionLoadContext(manifest.Id, path).LoadFromAssemblyPath(path);
            var entries = assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPanelExtension).IsAssignableFrom(type)).ToList();
            Check(entries.Count == 1, string.Format(ServerText.ExtensionEntryCount, entries.Count));
            var violation = ExtensionRules.RouteViolation(manifest.Id, assembly);
            Check(violation is null, violation ?? "");
            return new ExtensionStatus(folder, manifest, ExtensionState.Ok, Entry: entries[0]);
        }
        catch (Exception failure)
        {
            return new ExtensionStatus(folder, manifest, ExtensionState.Invalid, ReasonOf(failure));
        }
    }

    public static ExtensionCatalog Load(CtxUiOptions options)
    {
        List<LoadedExtension> loaded = [];
        List<string> disabled = [];
        List<RejectedExtension> rejected = [];
        foreach (var status in Scan(options))
        {
            if (status.State == ExtensionState.Disabled)
            {
                disabled.Add(status.Name);
                continue;
            }

            if (status is not { State: ExtensionState.Ok, Manifest: { } manifest, Entry: { } type })
            {
                rejected.Add(new RejectedExtension(status.Name, status.Problem ?? status.State.ToString()));
                continue;
            }

            try
            {
                var entry = (IPanelExtension)Activator.CreateInstance(type)!;
                loaded.Add(new LoadedExtension(manifest, status.Folder, Path.Combine(Path.GetFullPath(options.DataDir), DataFolder, manifest.Id), entry));
            }
            catch (Exception failure)
            {
                rejected.Add(new RejectedExtension(status.Name, ReasonOf(failure)));
            }
        }

        return new ExtensionCatalog(loaded, disabled, rejected);
    }

    private static string ReasonOf(Exception failure) => failure switch
    {
        ReflectionTypeLoadException { LoaderExceptions: [{ } first, ..] } => first.Message,
        _ => failure.GetBaseException().Message,
    };

    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidDataException(reason);
    }
}
