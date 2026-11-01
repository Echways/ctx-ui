using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtxUi.Extensions;

namespace CtxUi.Server.Tests;

public partial class ExtensionPackagingTests
{
    private sealed record Manifest(string Id, string Name, string Version, string Api, string Assembly);

    private static string Folder(string id) => Path.Combine(AppContext.BaseDirectory, "extensions", id);

    private static List<string> PublishedFiles(string id) =>
        [.. Directory.EnumerateFiles(Folder(id), "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Folder(id), file).Replace('\\', '/'))];

    private static Manifest ReadManifest(string id) =>
        JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(Folder(id), "extension.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [GeneratedRegex(@"(^|/)(CtxUi\.|Enfilade|MudBlazor|Jeffijoe\.MessageFormat|Microsoft\.EntityFrameworkCore|Microsoft\.Data\.Sqlite|SQLitePCLRaw)|^runtimes/|^wwwroot/_content/")]
    private static partial Regex SharedFile();

    [Fact]
    public void The_published_folder_holds_only_what_the_host_lacks()
    {
        var files = PublishedFiles("sample");
        Assert.Contains("SampleExtension.dll", files);
        Assert.Contains("extension.json", files);
        Assert.Contains("UAParser.dll", files);
        Assert.Contains("ru/SampleExtension.resources.dll", files);
        Assert.DoesNotContain(files, file => SharedFile().IsMatch(file));
    }

    [Fact]
    public void The_manifest_promises_the_api_version_of_the_contract()
    {
        var manifest = ReadManifest("sample");
        Assert.Equal("sample", manifest.Id);
        Assert.Equal("Sample", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("SampleExtension", manifest.Assembly);
        Assert.Equal(ExtensionApi.Version.ToString(2), manifest.Api);
    }

    [Fact]
    public void The_archive_holds_the_manifest_and_assembly_at_its_root()
    {
        using var archive = ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory, "extensions", "sample-1.2.3.zip"));
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("extension.json", names);
        Assert.Contains("SampleExtension.dll", names);
        Assert.Contains("wwwroot/sample.css", names);
    }
}
