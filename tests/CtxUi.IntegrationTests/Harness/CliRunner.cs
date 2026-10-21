using Microsoft.Extensions.Configuration;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

internal static class CliRunner
{
    public static Task<(int Code, string Output, string Error)> RunAsync(string dataDir, params string[] args) =>
        RunInLanguageAsync("ru", dataDir, args);

    public static async Task<(int Code, string Output, string Error)> RunInLanguageAsync(string language, string dataDir, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(args, output, error, builder =>
        {
            builder.Configuration["CtxUi:DataDir"] = dataDir;
            builder.Configuration["CtxUi:PublicUrl"] = "https://mcp.example.org/";
            builder.Configuration["CtxUi:Language"] = language;
        });
        return (code, output.ToString(), error.ToString());
    }
}
