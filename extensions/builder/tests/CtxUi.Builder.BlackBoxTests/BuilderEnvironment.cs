using System.Diagnostics;

namespace CtxUi.Builder.BlackBoxTests;

internal static class BuilderEnvironment
{
    public const string Requirements =
        "Set CTXUI_BINARY to a ctx-ui server binary and run a Docker Engine 29 or newer to run the builder flow tests";

    private const string BinaryVariable = "CTXUI_BINARY";
    private const int MinimumDockerMajorVersion = 29;

    private static readonly Lazy<bool> DockerIsReady = new(CheckDocker);

    public static bool Ready =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BinaryVariable)) && DockerIsReady.Value;

    public static bool PanelBinaryIsSet => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BinaryVariable));

    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CtxUi.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new InvalidOperationException(
                   "The repository root with CtxUi.slnx was not found above the test output");
    }

    private static bool CheckDocker()
    {
        try
        {
            var info = new ProcessStartInfo("docker")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("version");
            info.ArgumentList.Add("--format");
            info.ArgumentList.Add("{{.Server.Version}}");
            using var process = Process.Start(info)!;
            var version = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && int.TryParse(version.Split('.')[0], out var major) &&
                   major >= MinimumDockerMajorVersion;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
