using System.Security.Cryptography;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using Enfilade;

namespace CtxUi.Builder.Tests;

public sealed class ReleaseArchivesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Written_archive_has_its_final_name_and_leaves_no_temporary_file()
    {
        var archives = new ReleaseArchives(_directory);
        var id = Guid.NewGuid();

        await archives.WriteAsync(id, [1, 2, 3], default);

        Assert.Equal(Path.Combine(_directory, "releases", $"{id}.tar.gz"), archives.PathOf(id));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(archives.PathOf(id)));
        Assert.Equal([archives.PathOf(id)], Directory.GetFiles(Path.Combine(_directory, "releases")));
    }

    [Fact]
    public async Task Interrupted_temporary_files_are_removed()
    {
        var archives = new ReleaseArchives(_directory);
        await archives.WriteAsync(Guid.NewGuid(), [1], default);
        var leftover = Path.Combine(_directory, "releases", $"{Guid.NewGuid()}.tar.gz.tmp");
        await File.WriteAllBytesAsync(leftover, [9]);

        archives.RemoveInterrupted();

        Assert.False(File.Exists(leftover));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "releases")));
    }

    [Fact]
    public async Task Opening_verifies_the_hash()
    {
        var archives = new ReleaseArchives(_directory);
        var id = Guid.NewGuid();
        var content = new byte[] { 1, 2, 3 };
        await archives.WriteAsync(id, content, default);
        var release = new Release { Id = id, ArchiveHash = Convert.ToHexString(SHA256.HashData(content)) };

        await using (var stream = await archives.OpenVerifiedAsync(release, default))
        {
            Assert.Equal(0, stream.Position);
            Assert.Equal(3, stream.Length);
        }

        await File.WriteAllBytesAsync(archives.PathOf(id), [1, 2, 4]);
        var corrupted = await Assert.ThrowsAsync<PanelException>(() => archives.OpenVerifiedAsync(release, default));
        Assert.Equal(BuilderText.ArchiveCorrupted, corrupted.Message);
    }

    [Fact]
    public async Task A_missing_archive_is_reported()
    {
        var archives = new ReleaseArchives(_directory);

        var missing = await Assert.ThrowsAsync<PanelException>(() => archives.OpenVerifiedAsync(new Release { Id = Guid.NewGuid(), ArchiveHash = "00" }, default));

        Assert.Equal(BuilderText.ArchiveMissing, missing.Message);
    }
}
