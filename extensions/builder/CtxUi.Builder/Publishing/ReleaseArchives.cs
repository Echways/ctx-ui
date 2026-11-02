using System.Security.Cryptography;
using CtxUi.Builder.Data;
using Enfilade;

namespace CtxUi.Builder.Publishing;

public sealed class ReleaseArchives(string dataDirectory)
{
    private const string TemporarySuffix = ".tmp";

    private readonly string _directory = Path.Combine(dataDirectory, "releases");

    public string PathOf(Guid releaseId) => Path.Combine(_directory, $"{releaseId}.tar.gz");

    public async Task WriteAsync(Guid releaseId, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var temporary = PathOf(releaseId) + TemporarySuffix;
        await File.WriteAllBytesAsync(temporary, content, ct);
        File.Move(temporary, PathOf(releaseId), true);
    }

    public void Remove(Guid releaseId)
    {
        if (!Directory.Exists(_directory))
            return;
        File.Delete(PathOf(releaseId));
        File.Delete(PathOf(releaseId) + TemporarySuffix);
    }

    public void RemoveInterrupted()
    {
        if (!Directory.Exists(_directory))
            return;
        foreach (var file in Directory.EnumerateFiles(_directory, $"*{TemporarySuffix}"))
            File.Delete(file);
    }

    public async Task<FileStream> OpenVerifiedAsync(Release release, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(PathOf(release.Id), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PanelException(BuilderText.ArchiveMissing);
        }

        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(hash, release.ArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new PanelException(BuilderText.ArchiveCorrupted);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
