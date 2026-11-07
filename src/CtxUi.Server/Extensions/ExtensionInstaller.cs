using System.IO.Compression;
using CtxUi.Core;

namespace CtxUi.Server.Extensions;

public sealed class ExtensionInstaller(string extensionsDir, string dataDir)
{
    public ExtensionManifest Install(string archive)
    {
        if (!File.Exists(archive))
            throw new DomainException(string.Format(ServerText.ExtensionArchiveMissing, archive));

        ServiceRegistration.CreatePrivateDirectory(extensionsDir);
        var unpacked = Path.Combine(extensionsDir, $".install-{Guid.NewGuid():N}");
        try
        {
            Unpack(archive, unpacked);
            var status = ExtensionCatalog.Inspect(unpacked);
            if (status is not { State: ExtensionState.Ok, Manifest: { } manifest })
                throw new DomainException(status.Problem ?? status.State.ToString());

            Replace(Path.Combine(extensionsDir, manifest.Id), unpacked);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(unpacked))
                Directory.Delete(unpacked, true);
        }
    }

    public void Remove(string id, bool purge)
    {
        var folder = Path.Combine(extensionsDir, id);
        if (!ExtensionRules.IsValidId(id) || !Directory.Exists(folder))
            throw new DomainException(string.Format(ServerText.ExtensionUnknown, id));

        Directory.Delete(folder, true);
        var data = Path.Combine(dataDir, ExtensionCatalog.DataFolder, id);
        if (purge && Directory.Exists(data))
            Directory.Delete(data, true);
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

        Directory.Delete(previous, true);
    }
}
