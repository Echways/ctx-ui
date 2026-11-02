using System.Diagnostics;

namespace CtxUi.Builder.BlackBoxTests;

internal static class Shell
{
    public static async Task<string> RunAsync(string file, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? limit = null)
    {
        var (code, output) = await TryRunAsync(file, arguments, environment, limit);
        return code == 0 ? output : throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} exited with {code}: {output}");
    }

    public static async Task<(int Code, string Output)> TryRunAsync(string file, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? limit = null)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            info.Environment[key] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}");
        using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromMinutes(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} {string.Join(' ', arguments)} did not finish in time");
        }

        return (process.ExitCode, await output + await error);
    }
}
