using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CtxUi.Builder.BlackBoxTests;

internal sealed class ExportedProject : IAsyncDisposable
{
    private const int ContainerPort = 8000;
    private static readonly TimeSpan StartPatience = TimeSpan.FromMinutes(4);

    private readonly string _folder = Directory.CreateTempSubdirectory("ctxb-export-").FullName;
    private readonly string _container = $"ctxb-blackbox-export-{Guid.NewGuid():N}"[..40];
    private readonly int _port = FreePort();

    public async Task RunAsync(byte[] archive, string baseUrl)
    {
        await using (var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gzip, _folder, overwriteFiles: true);
        MakeReadableByEveryone(_folder);

        await Shell.RunAsync("docker",
        [
            "run", "-d", "--name", _container,
            "-p", $"127.0.0.1:{_port}:{ContainerPort}",
            "-v", $"{_folder}:/export:ro",
            "-w", "/export",
            "-e", "HOME=/tmp",
            "--tmpfs", "/tmp:exec,mode=1777",
            "-e", $"BUILDER_BASE_URL={baseUrl}",
            RunnerStack.RuntimeImage,
            "uvx", "--from", $"fastmcp=={FastMcpVersion}", "fastmcp", "run", "fastmcp.json",
        ]);
    }

    public async Task<string> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp") }));
                var result = await mcp.CallToolAsync(tool, arguments);
                return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            }
            catch (Exception failure) when (waited.Elapsed < StartPatience && failure is HttpRequestException or IOException or InvalidOperationException or ModelContextProtocol.McpException)
            {
                var (_, running) = await Shell.TryRunAsync("docker", ["inspect", "-f", "{{.State.Running}}", _container]);
                if (running.Trim() != "true")
                    throw new InvalidOperationException($"The exported project stopped: {await LogAsync()}", failure);
                await Task.Delay(2000);
            }
        }
    }

    public async Task<string> LogAsync() => (await Shell.TryRunAsync("docker", ["logs", "--tail", "40", _container])).Output;

    public async ValueTask DisposeAsync()
    {
        await Shell.TryRunAsync("docker", ["rm", "-f", _container]);
        Directory.Delete(_folder, recursive: true);
    }

    public static string FastMcpVersion => "4.1.4";

    private static void MakeReadableByEveryone(string folder)
    {
        const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(folder, Open);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(entry, Open);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
