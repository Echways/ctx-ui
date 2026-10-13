using Microsoft.Extensions.Configuration;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

internal static class CliRunner
{
    public static async Task<(int Code, string Output, string Error)> RunAsync(string dataDir, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(args, output, error, builder =>
        {
            builder.Configuration["CtxUi:DataDir"] = dataDir;
            builder.Configuration["CtxUi:PublicUrl"] = "https://mcp.example.org/";
        });
        return (code, output.ToString(), error.ToString());
    }
}
