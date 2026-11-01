using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CtxUi.Builder.Contracts;
using Docker.DotNet.Models;

namespace CtxUi.Builder.Runner;

public static class ContainerTemplate
{
    private const string LabelRoot = "ctx-ui.builder";
    private const string User = "10001";
    private const int ProxyPort = 3128;
    private const string NoProxy = "127.0.0.1,localhost";
    private const string CacheDirectory = "/uv-cache";
    private const string GatewayModeOption = "com.docker.network.bridge.gateway_mode_ipv4";
    private const int EnvironmentHashLength = 12;

    private static readonly string[] ServerCommand = ["/env/.venv/bin/fastmcp", "run", "/code/fastmcp.json", "--skip-env", "--no-banner"];
    private static readonly string[] BuildCommand = ["python", "/opt/builder/prepare.py"];

    private static readonly HashSet<string> ReservedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTTPS_PROXY",
        "HTTP_PROXY",
        "NO_PROXY",
        "FASTMCP_STATELESS_HTTP",
        "PATH",
        "LD_PRELOAD"
    };

    public static string Prefix(Guid id) => $"ctxb-{id:N}";

    public static string NetworkName(Guid id, NetworkAccess access) =>
        $"{Prefix(id)}-{(access == NetworkAccess.InternetOnly ? "internet" : "full")}";

    public static CreateContainerParameters Server(Guid id, int release, string environmentHash, ServerSpec spec,
        IReadOnlyDictionary<string, string> environment, RunnerOptions options)
    {
        var labels = Labels(id);
        labels[$"{LabelRoot}.release"] = release.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.environment-hash"] = environmentHash;
        labels[$"{LabelRoot}.network"] = spec.Network.ToString();
        labels[$"{LabelRoot}.memory-mb"] = spec.Limits.MemoryMb.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.cpus"] = spec.Limits.Cpus.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.pids"] = spec.Limits.Pids.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.route-secret-sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(spec.RouteSecret)));

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            if (key.Length > 0 && !key.Contains('=') && !ReservedVariables.Contains(key))
            {
                variables[key] = value;
            }
        }

        variables["FASTMCP_STATELESS_HTTP"] = "true";
        variables["UV_CACHE_DIR"] = CacheDirectory;
        AddProxy(variables, spec.Network, options);

        return Create(
            $"{Prefix(id)}-r{release}",
            id,
            spec,
            options,
            labels,
            variables,
            ServerCommand,
            [
                VolumeMount($"{Prefix(id)}-code-{release}", "/code", readOnly: true),
                VolumeMount(EnvironmentVolume(id, environmentHash), "/env", readOnly: true),
                VolumeMount($"{Prefix(id)}-data", "/data", readOnly: false),
                TmpfsMount()
            ]);
    }

    public static CreateContainerParameters Build(Guid id, int release, string environmentHash, ServerSpec spec, RunnerOptions options)
    {
        var labels = Labels(id);
        labels[$"{LabelRoot}.release"] = release.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.environment-hash"] = environmentHash;
        labels[$"{LabelRoot}.role"] = "build";

        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["UV_CACHE_DIR"] = CacheDirectory };
        AddProxy(variables, spec.Network, options);

        return Create(
            $"{Prefix(id)}-b{release}",
            id,
            spec,
            options,
            labels,
            variables,
            BuildCommand,
            [
                VolumeMount($"{Prefix(id)}-code-{release}", "/code", readOnly: true),
                VolumeMount(EnvironmentVolume(id, environmentHash), "/env", readOnly: false),
                VolumeMount($"{Prefix(id)}-uvcache", CacheDirectory, readOnly: false),
                TmpfsMount()
            ]);
    }

    public static NetworksCreateParameters Network(Guid id, NetworkAccess access, string subnet)
    {
        var parameters = new NetworksCreateParameters
        {
            Name = NetworkName(id, access),
            Driver = "bridge",
            Internal = access == NetworkAccess.InternetOnly,
            Labels = Labels(id),
            IPAM = new IPAM { Driver = "default", Config = [new IPAMConfig { Subnet = subnet }] }
        };

        if (access == NetworkAccess.InternetOnly)
        {
            parameters.Options = new Dictionary<string, string> { [GatewayModeOption] = "isolated" };
        }

        return parameters;
    }

    public static VolumesCreateParameters Volume(Guid id, string suffix) =>
        new() { Name = $"{Prefix(id)}-{suffix}", Labels = Labels(id) };

    private static CreateContainerParameters Create(string name, Guid id, ServerSpec spec, RunnerOptions options,
        Dictionary<string, string> labels, Dictionary<string, string> variables, IList<string> command, IList<Mount> mounts) =>
        new()
        {
            Name = name,
            Image = options.RuntimeImage,
            User = User,
            Cmd = command,
            Env = variables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}").ToList(),
            Labels = labels,
            HostConfig = new HostConfig
            {
                Privileged = false,
                ReadonlyRootfs = true,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges"],
                Memory = spec.Limits.MemoryMb * 1024L * 1024L,
                NanoCPUs = (long)Math.Round(spec.Limits.Cpus * 1_000_000_000d),
                PidsLimit = spec.Limits.Pids,
                NetworkMode = NetworkName(id, spec.Network),
                Runtime = options.ContainerRuntime,
                Mounts = mounts
            }
        };

    private static void AddProxy(Dictionary<string, string> variables, NetworkAccess access, RunnerOptions options)
    {
        if (access != NetworkAccess.InternetOnly)
        {
            return;
        }

        var proxy = $"http://{options.EgressContainer}:{ProxyPort}";
        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy" })
        {
            variables[name] = proxy;
        }

        variables["NO_PROXY"] = NoProxy;
        variables["no_proxy"] = NoProxy;
    }

    private static Dictionary<string, string> Labels(Guid id) => new() { [LabelRoot] = id.ToString() };

    private static string EnvironmentVolume(Guid id, string environmentHash) =>
        $"{Prefix(id)}-env-{environmentHash[..Math.Min(EnvironmentHashLength, environmentHash.Length)]}";

    private static Mount VolumeMount(string source, string target, bool readOnly) =>
        new() { Type = "volume", Source = source, Target = target, ReadOnly = readOnly };

    private static Mount TmpfsMount() => new() { Type = "tmpfs", Target = "/tmp" };
}
