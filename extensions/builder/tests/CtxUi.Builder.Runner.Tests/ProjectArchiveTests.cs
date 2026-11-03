using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace CtxUi.Builder.Runner.Tests;

public sealed class ProjectArchiveTests
{
    private static string Hash(string environment) =>
        ProjectArchive.EnvironmentHash(Project.Archive("{\"environment\":" + environment + "}"));

    private static byte[] WithEntry(TarEntry entry)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "fastmcp.json")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"environment":{}}"""))
            });
            tar.WriteEntry(entry);
        }

        return compressed.ToArray();
    }

    [Fact]
    public void Order_of_dependencies_and_keys_does_not_change_the_hash()
    {
        Assert.Equal(
            Hash("""{"python":"3.13","dependencies":["a","b"]}"""),
            Hash("""{"dependencies":["b","a"],"python":"3.13"}"""));
    }

    [Theory]
    [InlineData("""{"python":"3.12","dependencies":["a"]}""")]
    [InlineData("""{"python":"3.13","dependencies":["a==2"]}""")]
    [InlineData("""{"python":"3.13","dependencies":["a","b"]}""")]
    public void Python_or_dependency_change_changes_the_hash(string other)
    {
        Assert.NotEqual(Hash("""{"python":"3.13","dependencies":["a"]}"""), Hash(other));
    }

    [Fact]
    public void Code_changes_do_not_change_the_hash()
    {
        const string config = """{"environment":{"dependencies":["a"]}}""";

        Assert.Equal(
            ProjectArchive.EnvironmentHash(Project.Archive(config, ("mcp/a.py", "1"))),
            ProjectArchive.EnvironmentHash(Project.Archive(config, ("mcp/a.py", "2"))));
    }

    [Theory]
    [InlineData("../evil.py")]
    [InlineData("/etc/passwd")]
    [InlineData("mcp/../../evil.py")]
    public void Paths_outside_the_project_are_refused(string path)
    {
        var archive = WithEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream([1]) });

        var failure = Assert.Throws<ProjectArchiveException>(() => ProjectArchive.EnvironmentHash(archive));

        Assert.Contains(path, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public void Links_are_refused(TarEntryType type)
    {
        var archive = WithEntry(new PaxTarEntry(type, "link") { LinkName = "/etc/passwd" });

        Assert.Throws<ProjectArchiveException>(() => ProjectArchive.EnvironmentHash(archive));
    }

    [Fact]
    public void Archive_that_unpacks_far_beyond_the_project_limit_is_refused()
    {
        var bomb = WithEntry(new PaxTarEntry(TarEntryType.RegularFile, "mcp/bomb.bin")
        {
            DataStream = new MemoryStream(new byte[ProjectArchive.MaxUnpackedBytes + 1])
        });
        Assert.True(bomb.Length < 1024 * 1024);

        var failure = Assert.Throws<ProjectArchiveException>(() => ProjectArchive.EnvironmentHash(bomb));

        Assert.Contains("limit", failure.Message, StringComparison.Ordinal);
    }
}
