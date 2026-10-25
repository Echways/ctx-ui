using Microsoft.Extensions.Configuration;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

internal static class CliRunner
{
    public const string DefaultPublicUrl = "https://mcp.example.org/";

    public static Task<(int Code, string Output, string Error)> RunAsync(string dataDir, params string[] args) =>
        RunInLanguageAsync("ru", dataDir, args);

    public static Task<(int Code, string Output, string Error)> RunInLanguageAsync(string language, string dataDir, params string[] args) =>
        RunWithAsync(language, DefaultPublicUrl, null, dataDir, args);

    public static Task<(int Code, string Output, string Error)> RunWithPublicUrlAsync(string publicUrl, string dataDir, params string[] args) =>
        RunWithAsync("ru", publicUrl, null, dataDir, args);

    public static Task<(int Code, string Output, string Error)> RunWithInputAsync(string input, string dataDir, params string[] args) =>
        RunWithAsync("ru", DefaultPublicUrl, new StringReader(input), dataDir, args);

    private static async Task<(int Code, string Output, string Error)> RunWithAsync(
        string language,
        string publicUrl,
        TextReader? input,
        string dataDir,
        string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Cli.RunAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Configuration["CtxUi:DataDir"] = dataDir;
                builder.Configuration["CtxUi:PublicUrl"] = publicUrl;
                builder.Configuration["CtxUi:Language"] = language;
            },
            input);
        return (code, output.ToString(), error.ToString());
    }
}
