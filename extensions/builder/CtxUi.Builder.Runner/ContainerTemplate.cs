using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CtxUi.Builder.Contracts;
using Docker.DotNet.Models;

namespace CtxUi.Builder.Runner;

public static class BuilderLabels
{
    public const string Root = "ctx-ui.builder";
    public const string SystemValue = "system";
    public const string Release = $"{Root}.release";
    public const string EnvironmentHash = $"{Root}.environment-hash";
    public const string Network = $"{Root}.network";
    public const string RouteSecretHash = $"{Root}.route-secret-sha256";
    public const string Role = $"{Root}.role";
    public const string Activation = $"{Root}.activation";
    public const string ActiveContainer = $"{Root}.active-container";
    public const string RetiringContainers = $"{Root}.retiring-containers";
    public const char ListSeparator = ',';
    public const string BuildRole = "build";
    public const string UnpackRole = "unpack";
    public const string ActivationRole = "activation";
}

public static class ContainerTemplate
{
    private const string LabelRoot = BuilderLabels.Root;
    private const string User = "10001";
    private const int ProxyPort = 3128;
    private const string NoProxy = "127.0.0.1,localhost";
    private const string CacheDirectory = "/uv-cache";
    private const string GatewayModeOption = "com.docker.network.bridge.gateway_mode_ipv4";
    private const int EnvironmentHashLength = 12;
    private const int MinimumMemoryMb = 6;
    private const double MinimumCpus = 0.01;
    private const string NoNetwork = "none";
    private const long BytesInMegabyte = 1024L * 1024L;

    private static readonly string[] ServerCommand =
        ["/env/.venv/bin/fastmcp", "run", "/code/fastmcp.json", "--skip-env", "--no-banner"];

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

    public static string RouteSecretHash(string secret)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    public static string Prefix(Guid id)
    {
        return $"ctxb-{id:N}";
    }

    public static string NetworkName(Guid id, NetworkAccess access)
    {
        return $"{Prefix(id)}-{(access == NetworkAccess.InternetOnly ? "internet" : "full")}";
    }

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
        labels[BuilderLabels.RouteSecretHash] = RouteSecretHash(spec.RouteSecret);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
            if (key.Length > 0 && !key.Contains('=') && !ReservedVariables.Contains(key))
                variables[key] = value;

        variables["FASTMCP_STATELESS_HTTP"] = "true";
        variables["UV_CACHE_DIR"] = CacheDirectory;
        AddProxy(variables, spec.Network, options);

        var parameters = Create(
            $"{Prefix(id)}-r{release}",
            id,
            spec,
            options,
            labels,
            variables,
            ServerCommand,
            [
                VolumeMount(id, CodeVolume(id, release), "/code", true),
                VolumeMount(id, EnvironmentVolume(id, environmentHash), "/env", true),
                VolumeMount(id, DataVolume(id), "/data", false),
                TmpfsMount()
            ]);
        parameters.HostConfig.RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped };
        return parameters;
    }

    public static CreateContainerParameters Build(Guid id, int release, string environmentHash, ServerSpec spec,
        RunnerOptions options)
    {
        var labels = Labels(id);
        labels[$"{LabelRoot}.release"] = release.ToString(CultureInfo.InvariantCulture);
        labels[$"{LabelRoot}.environment-hash"] = environmentHash;
        labels[BuilderLabels.Role] = BuilderLabels.BuildRole;

        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["UV_CACHE_DIR"] = CacheDirectory };
        AddProxy(variables, spec.Network, options);

        return Create(
            BuildContainerName(id, release),
            id,
            spec,
            options,
            labels,
            variables,
            BuildCommand,
            [
                VolumeMount(id, CodeVolume(id, release), "/code", true),
                VolumeMount(id, EnvironmentVolume(id, environmentHash), "/env", false),
                VolumeMount(id, CacheVolume(id), CacheDirectory, false),
                TmpfsMount()
            ]);
    }

    public static CreateContainerParameters Unpack(Guid id, int release, string environmentHash, RunnerOptions options)
    {
        var labels = Labels(id);
        labels[BuilderLabels.Release] = release.ToString(CultureInfo.InvariantCulture);
        labels[BuilderLabels.EnvironmentHash] = environmentHash;
        labels[BuilderLabels.Role] = BuilderLabels.UnpackRole;
        return NeverStarted(
            $"{Prefix(id)}-u{release}",
            labels,
            options,
            [
                VolumeMount(id, CodeVolume(id, release), "/code", false),
                VolumeMount(id, EnvironmentVolume(id, environmentHash), "/env", true)
            ]);
    }

    public static VolumesCreateParameters ActivationMarker(Guid id, int activation, int release, string activeContainer,
        IReadOnlyCollection<string> retiringContainers)
    {
        var labels = Labels(id);
        labels[BuilderLabels.Role] = BuilderLabels.ActivationRole;
        labels[BuilderLabels.Activation] = activation.ToString(CultureInfo.InvariantCulture);
        labels[BuilderLabels.Release] = release.ToString(CultureInfo.InvariantCulture);
        labels[BuilderLabels.ActiveContainer] = activeContainer;
        labels[BuilderLabels.RetiringContainers] = string.Join(BuilderLabels.ListSeparator, retiringContainers);
        return new VolumesCreateParameters { Name = $"{Prefix(id)}-active-{activation}", Labels = labels };
    }

    public static void EnsureSafeLimits(ServerLimits limits)
    {
        if (limits.MemoryMb < MinimumMemoryMb)
            throw new ArgumentOutOfRangeException(nameof(limits), limits.MemoryMb,
                $"Memory limit must be at least {MinimumMemoryMb} MB.");

        if (!double.IsFinite(limits.Cpus) || limits.Cpus < MinimumCpus)
            throw new ArgumentOutOfRangeException(nameof(limits), limits.Cpus,
                $"CPU limit must be a finite number of at least {MinimumCpus.ToString(CultureInfo.InvariantCulture)}.");

        if (limits.Pids < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), limits.Pids, "Process limit must be at least 1.");
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
            parameters.Options = new Dictionary<string, string> { [GatewayModeOption] = "isolated" };

        return parameters;
    }

    public static string BuildContainerName(Guid id, int release)
    {
        return $"{Prefix(id)}-b{release}";
    }

    public static string CodeVolume(Guid id, int release)
    {
        return $"{Prefix(id)}-code-{release}";
    }

    public static string EnvironmentVolume(Guid id, string environmentHash)
    {
        return $"{Prefix(id)}-env-{environmentHash[..Math.Min(EnvironmentHashLength, environmentHash.Length)]}";
    }

    public static string CacheVolume(Guid id)
    {
        return $"{Prefix(id)}-uvcache";
    }

    public static string DataVolume(Guid id)
    {
        return $"{Prefix(id)}-data";
    }

    public static VolumesCreateParameters Volume(Guid id, string name)
    {
        return new VolumesCreateParameters { Name = name, Labels = Labels(id) };
    }

    private static CreateContainerParameters Create(string name, Guid id, ServerSpec spec, RunnerOptions options,
        Dictionary<string, string> labels, Dictionary<string, string> variables, IList<string> command,
        IList<Mount> mounts)
    {
        EnsureSafeLimits(spec.Limits);
        var memory = spec.Limits.MemoryMb * BytesInMegabyte;
        return new CreateContainerParameters
        {
            Name = name,
            Image = options.RuntimeImage,
            User = User,
            Cmd = command,
            Env = variables.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")
                .ToList(),
            Labels = labels,
            HostConfig = new HostConfig
            {
                Privileged = false,
                ReadonlyRootfs = true,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges"],
                Memory = memory,
                MemorySwap = memory,
                LogConfig = new LogConfig
                {
                    Type = "json-file",
                    Config = new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "3" }
                },
                NanoCPUs = (long)Math.Round(spec.Limits.Cpus * 1_000_000_000d),
                PidsLimit = spec.Limits.Pids,
                NetworkMode = NetworkName(id, spec.Network),
                Runtime = options.ContainerRuntime,
                Mounts = mounts
            }
        };
    }

    private static CreateContainerParameters NeverStarted(string name, Dictionary<string, string> labels,
        RunnerOptions options, IList<Mount> mounts)
    {
        return new CreateContainerParameters
        {
            Name = name,
            Image = options.RuntimeImage,
            User = User,
            Labels = labels,
            HostConfig = new HostConfig
            {
                Privileged = false,
                ReadonlyRootfs = true,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges"],
                NetworkMode = NoNetwork,
                Mounts = mounts
            }
        };
    }

    private static void AddProxy(Dictionary<string, string> variables, NetworkAccess access, RunnerOptions options)
    {
        if (access != NetworkAccess.InternetOnly)
            return;

        var proxy = $"http://{options.EgressContainer}:{ProxyPort}";
        foreach (var name in new[] { "HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy" })
            variables[name] = proxy;

        variables["NO_PROXY"] = NoProxy;
        variables["no_proxy"] = NoProxy;
    }

    private static Dictionary<string, string> Labels(Guid id)
    {
        return new Dictionary<string, string> { [LabelRoot] = id.ToString() };
    }

    private static Mount VolumeMount(Guid id, string source, string target, bool readOnly)
    {
        return new Mount
        {
            Type = "volume",
            Source = source,
            Target = target,
            ReadOnly = readOnly,
            VolumeOptions = new VolumeOptions { Labels = Labels(id) }
        };
    }

    private static Mount TmpfsMount()
    {
        return new Mount { Type = "tmpfs", Target = "/tmp" };
    }
}
