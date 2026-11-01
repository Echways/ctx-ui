using System.IO.Compression;
using System.Text.Json;
using CtxUi.Core;

namespace CtxUi.Server.Extensions;

public sealed record ExtensionStatus(ExtensionManifest? Manifest, string Directory, string State);

public sealed class ExtensionInstaller(string extensionsDir, string dataDir)
{
    public const string Ok = "ok";
    public const string Disabled = "disabled";
    public const string Incompatible = "incompatible";
    public const string Invalid = "invalid";

    public ExtensionManifest Install(string archive)
    {
        if (!File.Exists(archive))
            throw new DomainException(string.Format(ServerText.ExtensionArchiveMissing, archive));

        ServiceRegistration.CreatePrivateDirectory(extensionsDir);
        var unpacked = Path.Combine(extensionsDir, $".install-{Guid.NewGuid():N}");
        try
        {
            Unpack(archive, unpacked);
            var manifest = ReadChecked(unpacked);
            Replace(Path.Combine(extensionsDir, manifest.Id), unpacked);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(unpacked))
                Directory.Delete(unpacked, recursive: true);
        }
    }

    public void Remove(string id, bool purge)
    {
        var folder = Path.Combine(extensionsDir, id);
        if (!ExtensionRules.IsValidId(id) || !Directory.Exists(folder))
            throw new DomainException(string.Format(ServerText.ExtensionUnknown, id));

        Directory.Delete(folder, recursive: true);
        var data = Path.Combine(dataDir, ExtensionCatalog.DataFolder, id);
        if (purge && Directory.Exists(data))
            Directory.Delete(data, recursive: true);
    }

    public IReadOnlyList<ExtensionStatus> List(IReadOnlySet<string> disabled)
    {
        if (!Directory.Exists(extensionsDir))
            return [];

        return
        [
            .. Directory.GetDirectories(extensionsDir)
                .Where(folder => !Path.GetFileName(folder).StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .Select(folder => StatusOf(folder, disabled)),
        ];
    }

    private static ExtensionStatus StatusOf(string folder, IReadOnlySet<string> disabled)
    {
        var manifest = TryRead(folder);
        if (manifest is null || !ExtensionRules.IsValidId(manifest.Id) || manifest.Id != Path.GetFileName(folder))
            return new ExtensionStatus(manifest, folder, Invalid);
        if (disabled.Contains(manifest.Id))
            return new ExtensionStatus(manifest, folder, Disabled);
        return new ExtensionStatus(manifest, folder, ExtensionRules.IsCompatible(manifest.Api) ? Ok : Incompatible);
    }

    private static ExtensionManifest? TryRead(string folder)
    {
        try
        {
            return ExtensionManifest.Read(folder);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static void Unpack(string archive, string unpacked)
    {
        ServiceRegistration.CreatePrivateDirectory(unpacked);
        try
        {
            ZipFile.ExtractToDirectory(archive, unpacked);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new DomainException(string.Format(ServerText.ExtensionArchiveBroken, archive, failure.Message));
        }
    }

    private static ExtensionManifest ReadChecked(string unpacked)
    {
        var manifest = TryRead(unpacked) ?? throw new DomainException(ServerText.ExtensionManifestBroken);
        if (!ExtensionRules.IsValidId(manifest.Id))
            throw new DomainException(string.Format(ServerText.ExtensionInvalidId, manifest.Id));
        if (ExtensionRules.IsReservedId(manifest.Id))
            throw new DomainException(string.Format(ServerText.ExtensionReservedId, manifest.Id));
        if (!ExtensionRules.IsCompatible(manifest.Api))
            throw new DomainException(string.Format(ServerText.ExtensionIncompatible, manifest.Api, CtxUi.Extensions.ExtensionApi.Version.ToString(2)));
        if (!File.Exists(Path.Combine(unpacked, manifest.Assembly + ".dll")))
            throw new DomainException(string.Format(ServerText.ExtensionAssemblyMissing, manifest.Assembly));
        return manifest;
    }

    private void Replace(string target, string unpacked)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(unpacked, target);
            return;
        }

        var previous = Path.Combine(extensionsDir, $".old-{Guid.NewGuid():N}");
        Directory.Move(target, previous);
        try
        {
            Directory.Move(unpacked, target);
        }
        catch (IOException)
        {
            Directory.Move(previous, target);
            throw;
        }

        Directory.Delete(previous, recursive: true);
    }
}
