using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CtxUi.Extensions.Testing;

public sealed partial class TestPanel : IAsyncDisposable
{
    private const string BinaryVariable = "CTXUI_BINARY";
    private const string Admin = "root";

    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(60);

    private readonly string _binary;
    private readonly string _dataDir;
    private readonly Dictionary<string, string> _environment;
    private readonly StringBuilder _log = new();
    private Process? _server;
    private string _password = "";

    private TestPanel(string binary, string dataDir, Dictionary<string, string> environment)
    {
        _binary = binary;
        _dataDir = dataDir;
        _environment = environment;
    }

    public string Url { get; private set; } = "";

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public static async Task<TestPanel> StartAsync(TestPanelOptions options)
    {
        var binary = Environment.GetEnvironmentVariable(BinaryVariable);
        if (string.IsNullOrEmpty(binary))
            throw new InvalidOperationException(
                $"Set {BinaryVariable} to the path of a ctx-ui server binary to run panel tests");

        binary = Path.GetFullPath(binary);
        var dataDir = Directory.CreateTempSubdirectory("ctx-ui-test-panel-").FullName;
        var address = $"http://127.0.0.1:{FreePort()}";
        var environment = new Dictionary<string, string>
        {
            ["CtxUi__DataDir"] = dataDir,
            ["CtxUi__ExtensionsDir"] = Path.GetFullPath(options.ExtensionsDir),
            ["CtxUi__PublicUrl"] = address,
            ["CtxUi__Language"] = options.Language,
            ["Urls"] = address
        };
        foreach (var (key, value) in options.Settings ?? new Dictionary<string, string>())
            environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;

        var panel = new TestPanel(binary, dataDir, environment);
        try
        {
            await panel.AddAdminAsync(address);
            await panel.StartServerAsync(address);
            return panel;
        }
        catch
        {
            await panel.DisposeAsync();
            throw;
        }
    }

    public async Task<string> CliAsync(params string[] args)
    {
        var (code, output, error) = await RunAsync(_environment, args);
        return code == 0
            ? output
            : throw new InvalidOperationException($"ctx-ui {string.Join(' ', args)} exited with {code}: {error}");
    }

    public async Task SignInAsync(IPage page)
    {
        await page.GotoAsync(Url + "login");
        await page.Locator("input[name='Input.Name']").FillAsync(Admin);
        await page.Locator("input[name='Input.Password']").FillAsync(_password);
        await page.Locator("button[type=submit]").ClickAsync();
        await page.GetByRole(AriaRole.Navigation).First.WaitForAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is { } server)
        {
            if (!server.HasExited)
                server.Kill(true);
            await server.WaitForExitAsync();
            server.Dispose();
        }

        Directory.Delete(_dataDir, true);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task AddAdminAsync(string address)
    {
        var english = new Dictionary<string, string>(_environment) { ["CtxUi__Language"] = "en" };
        var (code, output, error) = await RunAsync(english, "admin", "add", Admin);
        if (code != 0)
            throw new InvalidOperationException($"ctx-ui admin add exited with {code}: {error}");

        _password = PasswordLine().Match(output).Groups[1].Value;
        var path = PanelPathLine().Match(output).Groups[1].Value;
        if (_password.Length == 0 || path.Length == 0)
            throw new InvalidOperationException($"ctx-ui admin add printed no password or panel address: {output}");
        Url = $"{address}/{path}/";
    }

    private async Task StartServerAsync(string address)
    {
        var server = StartInfo(_environment);
        server.RedirectStandardOutput = server.RedirectStandardError = true;
        _server = Process.Start(server) ?? throw new InvalidOperationException($"Could not start {_binary}");
        _server.OutputDataReceived += (_, line) => Append(line.Data);
        _server.ErrorDataReceived += (_, line) => Append(line.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartPatience)
        {
            if (_server.HasExited)
                throw new InvalidOperationException($"ctx-ui exited with {_server.ExitCode}: {Log}");
            try
            {
                using var health = await http.GetAsync(address + "/healthz");
                if (health.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"ctx-ui did not start in {StartPatience}: {Log}");
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(Dictionary<string, string> environment,
        params string[] args)
    {
        var command = StartInfo(environment);
        command.RedirectStandardOutput = command.RedirectStandardError = true;
        foreach (var argument in args)
            command.ArgumentList.Add(argument);

        using var process = Process.Start(command) ?? throw new InvalidOperationException($"Could not start {_binary}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private ProcessStartInfo StartInfo(Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(_binary)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_binary)! };
        foreach (var (key, value) in environment)
            info.Environment[key] = value;
        return info;
    }

    private void Append(string? line)
    {
        if (line is null)
            return;
        lock (_log)
        {
            _log.AppendLine(line);
        }
    }

    [GeneratedRegex(@"Password:\s+(\S+)")]
    private static partial Regex PasswordLine();

    [GeneratedRegex(@"https?://[^/\s]+/([a-z0-9]{16})/")]
    private static partial Regex PanelPathLine();
}
