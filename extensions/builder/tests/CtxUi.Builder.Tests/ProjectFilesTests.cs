using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using CtxUi.Builder.Data;
using CtxUi.Builder.Servers;

namespace CtxUi.Builder.Tests;

public sealed class ProjectFilesTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Theory]
    [InlineData("../evil.py")]
    [InlineData("/etc/passwd")]
    [InlineData("mcp/../../x.py")]
    [InlineData("mcp\\x.py")]
    [InlineData("mcp/./x.py")]
    [InlineData("mcp//x.py")]
    [InlineData("mcp/")]
    [InlineData("mcp/x\n.py")]
    [InlineData("mcp/x.exe")]
    [InlineData("mcp/x")]
    [InlineData("main.py")]
    [InlineData("fastmcp.json")]
    [InlineData("")]
    public void Unsafe_path_is_refused(string path)
    {
        Assert.Throws<BuilderException>(() => ProjectFiles.ValidatePath(path));
    }

    [Theory]
    [InlineData("mcp/tools/get_data.py")]
    [InlineData("mcp/data/cities.json")]
    [InlineData("mcp/notes.md")]
    [InlineData("openapi.json")]
    [InlineData("builder.json")]
    public void Allowed_path_is_returned_unchanged(string path)
    {
        Assert.Equal(path, ProjectFiles.ValidatePath(path));
    }

    [Fact]
    public async Task Archive_with_traversal_or_symlink_is_refused_as_a_whole()
    {
        var traversal = TarGz(("mcp/ok.py", "x = 1"), ("mcp/../../evil.py", "boom"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(traversal, default));

        var absolute = TarGz(("mcp/ok.py", "x = 1"), ("/etc/passwd", "boom"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(absolute, default));

        var symlink =
            WithTarEntries(new PaxTarEntry(TarEntryType.SymbolicLink, "mcp/link.py") { LinkName = "/etc/passwd" },
                ("mcp/ok.py", "x = 1"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(symlink, default));

        var hardlink = WithTarEntries(new PaxTarEntry(TarEntryType.HardLink, "mcp/link.py") { LinkName = "mcp/ok.py" },
            ("mcp/ok.py", "x = 1"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(hardlink, default));
    }

    [Fact]
    public async Task Zip_with_traversal_is_refused_as_a_whole()
    {
        var archive = Zip(("mcp/ok.py", "x = 1"), ("mcp/../evil.py", "boom"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(archive, default));
    }

    [Fact]
    public async Task Archive_bigger_than_16_mb_after_unpacking_is_refused()
    {
        var half = new string('a', (int)(ProjectFiles.MaxProjectBytes / 2));
        var archive = TarGz(("mcp/a.py", half), ("mcp/b.py", half), ("mcp/c.py", "x"));
        var failure = await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(archive, default));
        Assert.Contains("16", failure.Message);

        var zipped = Zip(("mcp/a.py", half), ("mcp/b.py", half), ("mcp/c.py", "x"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(zipped, default));
    }

    [Fact]
    public async Task Archive_with_too_many_entries_is_refused()
    {
        var entries = Enumerable.Range(0, ProjectFiles.MaxArchiveEntries + 1).Select(i => ($"mcp/f{i}.py", ""))
            .ToArray();
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(TarGz(entries), default));
    }

    [Fact]
    public async Task Archive_with_invalid_utf8_is_refused_as_a_whole()
    {
        var archive = TarGzBytes(("mcp/ok.py", Utf8.GetBytes("x = 1")), ("mcp/bad.py", [0xff, 0xfe, 0x41]));
        var failure = await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(archive, default));
        Assert.Contains("mcp/bad.py", failure.Message);
    }

    [Fact]
    public async Task Archive_with_a_disallowed_file_type_under_mcp_is_refused()
    {
        var archive = TarGz(("mcp/ok.py", "x = 1"), ("mcp/tool.exe", "MZ"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(archive, default));
    }

    [Fact]
    public async Task Archive_with_duplicate_paths_is_refused()
    {
        var archive = TarGz(("mcp/a.py", "one"), ("mcp/a.py", "two"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(archive, default));
    }

    [Fact]
    public async Task Garbage_is_not_an_archive()
    {
        using var stream = new MemoryStream(Utf8.GetBytes("this is not an archive"));
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.ImportArchiveAsync(stream, default));
    }

    [Fact]
    public async Task Exported_project_imports_back_and_drops_runtime_files()
    {
        var archive = TarGz(
            ("fastmcp.json", "{}"),
            ("main.py", "print('mine')"),
            ("./mcp/tools/a.py", "a = 1"),
            ("mcp/data.json", "{\"k\": 1}"),
            ("openapi.json", "{\"openapi\": \"3.1.0\"}"),
            ("builder.json", "{}"),
            ("README.md", "readme"));

        var files = (await ProjectFiles.ImportArchiveAsync(archive, default)).Files;

        Assert.Equal(
            ["builder.json", "mcp/data.json", "mcp/tools/a.py", "openapi.json"],
            files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal("a = 1", files.Single(file => file.Path == "mcp/tools/a.py").Content);
    }

    [Fact]
    public async Task Zip_archive_is_read_like_tar()
    {
        var files = (await ProjectFiles.ImportArchiveAsync(
            Zip(("mcp/a.py", "a = 1"), ("main.py", "x"), ("openapi.json", "{}")), default)).Files;

        Assert.Equal(["mcp/a.py", "openapi.json"], files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Dependencies_of_an_imported_fastmcp_json_are_kept_without_the_fastmcp_pin()
    {
        var archive = TarGz(
            ("fastmcp.json",
                "{\"environment\": {\"dependencies\": [\"fastmcp==3.0.0\", \"httpx>=0.28\", \"pydantic\"]}}"),
            ("mcp/a.py", "a = 1"));

        var import = await ProjectFiles.ImportArchiveAsync(archive, default);

        Assert.Equal("httpx>=0.28\npydantic", import.Dependencies);
        Assert.Single(import.Files);
    }

    [Fact]
    public async Task Snapshot_contains_project_files_and_pins_fastmcp()
    {
        var draft = new ServerDraft
        {
            ServerId = Guid.NewGuid(),
            OpenApiJson = "{\"openapi\": \"3.1.0\"}",
            BuilderJson = null,
            Dependencies = "httpx>=0.28\n\nfastmcp==1.0\n  pydantic  \n"
        };
        DraftFile[] files =
        [
            new() { ServerId = draft.ServerId, Path = "mcp/a.py", Content = "a = 1" },
            new() { ServerId = draft.ServerId, Path = "main.py", Content = "print('user main')" },
            new() { ServerId = draft.ServerId, Path = "fastmcp.json", Content = "{}" }
        ];

        var bytes = await ProjectFiles.SnapshotAsync(draft, files, "4.1.4", default);
        var entries = ReadTarGz(bytes);

        Assert.Equal(["fastmcp.json", "main.py", "mcp/a.py", "openapi.json"],
            entries.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("\"fastmcp==4.1.4\"", entries["fastmcp.json"]);
        Assert.DoesNotContain("fastmcp==1.0", entries["fastmcp.json"]);
        Assert.Contains("\"httpx>=0.28\"", entries["fastmcp.json"]);
        Assert.Contains("\"pydantic\"", entries["fastmcp.json"]);
        Assert.Contains("\"python\": \"3.13\"", entries["fastmcp.json"]);
        Assert.Contains("\"entrypoint\": \"mcp\"", entries["fastmcp.json"]);
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime-main.py")), entries["main.py"]);
        Assert.DoesNotContain("user main", entries["main.py"]);
        Assert.Equal("a = 1", entries["mcp/a.py"]);
    }

    [Fact]
    public async Task Snapshot_is_byte_identical_for_the_same_project()
    {
        var draft = new ServerDraft { ServerId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UnixEpoch };
        DraftFile[] files = [new() { ServerId = draft.ServerId, Path = "mcp/a.py", Content = "a = 1" }];

        var first = await ProjectFiles.SnapshotAsync(draft, files, "4.1.4", default);
        var second = await ProjectFiles.SnapshotAsync(draft, files, "4.1.4", default);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Snapshot_refuses_a_dependency_that_is_an_installer_option()
    {
        var draft = new ServerDraft { ServerId = Guid.NewGuid(), Dependencies = "--index-url https://evil.example" };
        await Assert.ThrowsAsync<BuilderException>(() => ProjectFiles.SnapshotAsync(draft, [], "4.1.4", default));
    }

    [Fact]
    public async Task Snapshot_round_trips_through_import()
    {
        var draft = new ServerDraft
        { ServerId = Guid.NewGuid(), BuilderJson = "{\"prompts\": []}", Dependencies = "httpx" };
        DraftFile[] files = [new() { ServerId = draft.ServerId, Path = "mcp/a.py", Content = "a = 1" }];

        var bytes = await ProjectFiles.SnapshotAsync(draft, files, "4.1.4", default);
        var import = await ProjectFiles.ImportArchiveAsync(new MemoryStream(bytes), default);

        Assert.Equal(["builder.json", "mcp/a.py"],
            import.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal("httpx", import.Dependencies);
    }

    [Fact]
    public void New_tool_template_is_valid_for_publishing()
    {
        var template = ProjectFiles.NewToolTemplate("get-data");

        Assert.Contains("from fastmcp.tools import tool", template);
        Assert.Contains("@tool", template);
        Assert.Contains("def get_data(", template);
    }

    [Theory]
    [InlineData("get-data", "get_data")]
    [InlineData("Weather Now", "weather_now")]
    [InlineData("9lives", "tool_9lives")]
    public void Tool_name_becomes_a_python_identifier(string name, string function)
    {
        Assert.Equal(function, ProjectFiles.ToolFunctionName(name));
    }

    private static MemoryStream TarGz(params (string Path, string Content)[] entries)
    {
        return TarGzBytes([.. entries.Select(entry => (entry.Path, Utf8.GetBytes(entry.Content)))]);
    }

    private static MemoryStream TarGzBytes(params (string Path, byte[] Content)[] entries)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            foreach (var (path, content) in entries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                { DataStream = new MemoryStream(content) });
        }

        output.Position = 0;
        return output;
    }

    private static MemoryStream WithTarEntries(TarEntry special, params (string Path, string Content)[] entries)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            foreach (var (path, content) in entries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                { DataStream = new MemoryStream(Utf8.GetBytes(content)) });
            writer.WriteEntry(special);
        }

        output.Position = 0;
        return output;
    }

    private static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = zip.CreateEntry(path).Open();
                writer.Write(Utf8.GetBytes(content));
            }
        }

        output.Position = 0;
        return output;
    }

    private static Dictionary<string, string> ReadTarGz(byte[] bytes)
    {
        var result = new Dictionary<string, string>();
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                continue;
            using var content = new StreamReader(entry.DataStream!, Utf8);
            result[entry.Name] = content.ReadToEnd();
        }

        return result;
    }
}
