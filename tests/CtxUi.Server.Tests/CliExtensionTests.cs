namespace CtxUi.Server.Tests;

public sealed class CliExtensionTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-extension-").FullName;

    private static string SampleArchive => Path.Combine(AppContext.BaseDirectory, "extensions", "sample-1.2.3.zip");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dataDir, true);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Cli.RunAsync(args, output, error, builder =>
        {
            builder.Configuration["CtxUi:DataDir"] = _dataDir;
            builder.Configuration["CtxUi:Language"] = "ru";
        });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Extension_is_a_cli_command()
    {
        Assert.True(Cli.Handles(["extension", "list"]));
    }

    [Fact]
    public async Task Install_list_and_remove_work_and_remind_about_the_restart()
    {
        var installed = await RunAsync("extension", "install", SampleArchive);
        var listed = await RunAsync("extension", "list");
        var removed = await RunAsync("extension", "remove", "sample");
        var empty = await RunAsync("extension", "list");

        Assert.Equal((0, ""), (installed.Code, installed.Error));
        Assert.Contains("sample 1.2.3", installed.Output, StringComparison.Ordinal);
        Assert.Contains("перезапустите", installed.Output, StringComparison.Ordinal);
        Assert.Equal(0, listed.Code);
        Assert.Contains("sample", listed.Output, StringComparison.Ordinal);
        Assert.Contains("1.2.3", listed.Output, StringComparison.Ordinal);
        Assert.Contains("sample  1.2.3  ok", listed.Output, StringComparison.Ordinal);
        Assert.Equal(0, removed.Code);
        Assert.Contains("перезапустите", removed.Output, StringComparison.Ordinal);
        Assert.Contains("Расширения не установлены", empty.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_explains_why_an_extension_will_not_load()
    {
        await RunAsync("extension", "install", SampleArchive);
        File.WriteAllText(Path.Combine(_dataDir, "extensions", "sample", "SampleExtension.dll"), "not an assembly");

        var listed = await RunAsync("extension", "list");

        Assert.Equal(0, listed.Code);
        Assert.Contains("sample  1.2.3  invalid  ", listed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_exits_with_one_and_a_clear_message()
    {
        var missing = await RunAsync("extension", "install", Path.Combine(_dataDir, "missing.zip"));
        var unknown = await RunAsync("extension", "remove", "nothing");

        Assert.Equal(1, missing.Code);
        Assert.Contains("missing.zip", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, unknown.Code);
        Assert.Contains("nothing", unknown.Error, StringComparison.Ordinal);
    }
}
