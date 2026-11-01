using System.IO.Compression;
using CtxUi.Core;
using CtxUi.Server.Extensions;

namespace CtxUi.Server.Tests;

public sealed class ExtensionInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ctx-ui-installer-").FullName;

    private string Extensions => Path.Combine(_root, "data", "extensions");

    private string Data => Path.Combine(_root, "data");

    private ExtensionInstaller Installer => new(Extensions, Data);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Manifest(string id = "sample", string version = "1.0.0", string api = "1.0") =>
        $$"""{ "id": "{{id}}", "name": "Sample", "version": "{{version}}", "api": "{{api}}", "assembly": "SampleExtension" }""";

    private string Archive(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    private string GoodArchive(string version = "1.0.0") =>
        Archive(("extension.json", Manifest(version: version)), ("SampleExtension.dll", version), ("wwwroot/sample.css", "a{}"));

    private void AssertOnlyTheFirstVersionIsThere()
    {
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(Extensions, "sample", "SampleExtension.dll")));
        Assert.Equal(["sample"], Directory.GetDirectories(Extensions).Select(Path.GetFileName));
    }

    [Fact]
    public void Install_unpacks_into_a_folder_named_by_the_id()
    {
        var manifest = Installer.Install(GoodArchive());

        Assert.Equal("sample", manifest.Id);
        Assert.True(File.Exists(Path.Combine(Extensions, "sample", "extension.json")));
        Assert.True(File.Exists(Path.Combine(Extensions, "sample", "wwwroot", "sample.css")));
        Assert.Equal(["sample"], Directory.GetDirectories(Extensions).Select(Path.GetFileName));
    }

    [Fact]
    public void Install_replaces_an_older_version_and_keeps_its_data()
    {
        Installer.Install(GoodArchive());
        var data = Path.Combine(Data, "extension-data", "sample");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "data.db"), "kept");
        File.WriteAllText(Path.Combine(Extensions, "sample", "stale.dll"), "old");

        Installer.Install(GoodArchive("2.0.0"));

        Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(Extensions, "sample", "SampleExtension.dll")));
        Assert.False(File.Exists(Path.Combine(Extensions, "sample", "stale.dll")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(data, "data.db")));
        Assert.Equal(["sample"], Directory.GetDirectories(Extensions).Select(Path.GetFileName));
    }

    [Fact]
    public void A_broken_archive_leaves_the_installed_version_intact()
    {
        Installer.Install(GoodArchive());
        var corrupt = Path.Combine(_root, "corrupt.zip");
        File.WriteAllText(corrupt, "not a zip");
        string[] broken =
        [
            Archive(("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest(id: "Bad/Id")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest(id: "login")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest(api: "2.0")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest())),
            Archive(("extension.json", "not json")),
            corrupt,
            Path.Combine(_root, "missing.zip"),
        ];

        foreach (var archive in broken)
        {
            Assert.Throws<DomainException>(() => Installer.Install(archive));
            AssertOnlyTheFirstVersionIsThere();
        }
    }

    [Fact]
    public void An_archive_with_a_path_outside_the_folder_is_refused()
    {
        Installer.Install(GoodArchive());
        var archive = Archive(("extension.json", Manifest()), ("SampleExtension.dll", "x"), ("../../evil.txt", "evil"));

        Assert.Throws<DomainException>(() => Installer.Install(archive));

        Assert.False(File.Exists(Path.Combine(Data, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Extensions, "evil.txt")));
        AssertOnlyTheFirstVersionIsThere();
    }

    [Fact]
    public void Remove_keeps_data_and_purge_deletes_it()
    {
        var data = Path.Combine(Data, "extension-data", "sample");
        Installer.Install(GoodArchive());
        Directory.CreateDirectory(data);

        Installer.Remove("sample", purge: false);

        Assert.False(Directory.Exists(Path.Combine(Extensions, "sample")));
        Assert.True(Directory.Exists(data));

        Installer.Install(GoodArchive());
        Installer.Remove("sample", purge: true);

        Assert.False(Directory.Exists(data));
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("../../data")]
    public void Remove_of_an_unknown_id_is_a_clear_error(string id)
    {
        Directory.CreateDirectory(Path.Combine(Data, "data"));

        var failure = Assert.Throws<DomainException>(() => Installer.Remove(id, purge: true));

        Assert.Contains(id, failure.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(Data, "data")));
    }

    [Fact]
    public void List_reports_every_state()
    {
        void Folder(string name, string? manifest)
        {
            var folder = Path.Combine(Extensions, name);
            Directory.CreateDirectory(folder);
            if (manifest is not null)
                File.WriteAllText(Path.Combine(folder, "extension.json"), manifest);
        }

        Folder("fine", Manifest("fine"));
        Folder("off", Manifest("off"));
        Folder("old", Manifest("old", api: "2.0"));
        Folder("empty", null);
        Folder("mismatch", Manifest("other"));
        Folder(".install-1", null);

        var states = Installer.List(new HashSet<string> { "off" })
            .ToDictionary(status => Path.GetFileName(status.Directory), status => status.State);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["empty"] = ExtensionInstaller.Invalid,
                ["fine"] = ExtensionInstaller.Ok,
                ["mismatch"] = ExtensionInstaller.Invalid,
                ["off"] = ExtensionInstaller.Disabled,
                ["old"] = ExtensionInstaller.Incompatible,
            },
            states);
    }

    [Fact]
    public void List_of_a_missing_folder_is_empty() => Assert.Empty(Installer.List(new HashSet<string>()));
}
