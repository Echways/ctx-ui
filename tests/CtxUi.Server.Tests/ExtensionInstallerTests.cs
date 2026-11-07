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

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "extensions", "sample");

    private static string Manifest(string id = "sample", string version = "1.0.0", string api = "1.0")
    {
        return
            $$"""{ "id": "{{id}}", "name": "Sample", "version": "{{version}}", "api": "{{api}}", "assembly": "SampleExtension" }""";
    }

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

    private string SampleArchive(string manifest, params (string Name, string Text)[] extra)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(SampleFolder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(SampleFolder, file).Replace('\\', '/');
            if (name != ExtensionManifest.FileName)
                zip.CreateEntryFromFile(file, name);
        }

        foreach (var (name, text) in extra.Append((ExtensionManifest.FileName, manifest)))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    private string GoodArchive(string version = "1.0.0")
    {
        return SampleArchive(Manifest(version: version), ("installed.txt", version));
    }

    private void InstallFolder(string name, string? manifest = null)
    {
        var folder = Path.Combine(Extensions, name);
        foreach (var file in Directory.EnumerateFiles(SampleFolder, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(folder, Path.GetRelativePath(SampleFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        if (manifest is not null)
            File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), manifest);
    }

    private void AssertOnlyTheFirstVersionIsThere()
    {
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(Extensions, "sample", "installed.txt")));
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

        Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(Extensions, "sample", "installed.txt")));
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
            Archive(("extension.json", Manifest("Bad/Id")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest("login")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest(api: "2.0")), ("SampleExtension.dll", "x")),
            Archive(("extension.json", Manifest())),
            Archive(("extension.json", Manifest()), ("SampleExtension.dll", "not an assembly")),
            SampleArchive(Manifest("other")),
            Archive(("extension.json", "not json")),
            corrupt,
            Path.Combine(_root, "missing.zip")
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
        var archive = SampleArchive(Manifest(), ("../../evil.txt", "evil"));

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

        Installer.Remove("sample", false);

        Assert.False(Directory.Exists(Path.Combine(Extensions, "sample")));
        Assert.True(Directory.Exists(data));

        Installer.Install(GoodArchive());
        Installer.Remove("sample", true);

        Assert.False(Directory.Exists(data));
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("../../data")]
    public void Remove_of_an_unknown_id_is_a_clear_error(string id)
    {
        Directory.CreateDirectory(Path.Combine(Data, "data"));

        var failure = Assert.Throws<DomainException>(() => Installer.Remove(id, true));

        Assert.Contains(id, failure.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(Data, "data")));
    }

    [Fact]
    public void Scan_reports_what_the_server_will_do_with_every_folder()
    {
        InstallFolder("sample");
        InstallFolder("off", Manifest("off"));
        InstallFolder("old", Manifest("old", api: "2.0"));
        InstallFolder("routes", Manifest("routes"));
        InstallFolder("mismatch", Manifest("other"));
        InstallFolder("login", Manifest("login"));
        Directory.CreateDirectory(Path.Combine(Extensions, "empty"));
        Directory.CreateDirectory(Path.Combine(Extensions, ".install-1"));

        var statuses = ExtensionCatalog.Scan(new CtxUiOptions { DataDir = Data, DisabledExtensions = "off" })
            .ToDictionary(status => status.Name);

        Assert.Equal(
            new Dictionary<string, ExtensionState>
            {
                ["empty"] = ExtensionState.Invalid,
                ["login"] = ExtensionState.Invalid,
                ["mismatch"] = ExtensionState.Invalid,
                ["off"] = ExtensionState.Disabled,
                ["old"] = ExtensionState.Incompatible,
                ["routes"] = ExtensionState.Invalid,
                ["sample"] = ExtensionState.Ok
            },
            statuses.ToDictionary(pair => pair.Key, pair => pair.Value.State));
        Assert.Contains("/sample", statuses["routes"].Problem, StringComparison.Ordinal);
        Assert.Contains("2.0", statuses["old"].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_of_a_missing_folder_is_empty()
    {
        Assert.Empty(ExtensionCatalog.Scan(new CtxUiOptions { DataDir = Data }));
    }
}
