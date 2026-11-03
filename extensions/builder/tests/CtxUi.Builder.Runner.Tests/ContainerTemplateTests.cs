using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CtxUi.Builder.Contracts;
using Docker.DotNet.Models;

namespace CtxUi.Builder.Runner.Tests;

public sealed class ContainerTemplateTests
{
    private static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private const string Secret = "route-secret-value-123";
    private const string Hash = "0123456789abcdef0123";
    private static readonly RunnerOptions Options = new("token", "10.0.0.2", "ctx-ui-builder-runtime:1", "ctx-ui-builder-egress", "10.213.0.0/16", null);

    private static ServerSpec Spec(NetworkAccess access) => new(access, new ServerLimits(), Secret);

    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    private static Dictionary<string, string> Env(CreateContainerParameters created) =>
        created.Env.Select(entry => entry.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);

    [Theory]
    [InlineData(NetworkAccess.InternetOnly)]
    [InlineData(NetworkAccess.FullNetwork)]
    public void Server_container_never_gets_elevated_rights(NetworkAccess access)
    {
        var hostile = new Dictionary<string, string> { ["LD_PRELOAD"] = "/x", ["PATH"] = "/x" };
        var created = ContainerTemplate.Server(Id, 3, "abc", Spec(access), hostile, Options);

        Assert.False(created.HostConfig.Privileged);
        Assert.Null(created.HostConfig.Binds);
        Assert.Null(created.HostConfig.CapAdd);
        Assert.Equal(["ALL"], created.HostConfig.CapDrop);
        Assert.Contains("no-new-privileges", created.HostConfig.SecurityOpt);
        Assert.True(created.HostConfig.ReadonlyRootfs);
        Assert.Equal("10001", created.User);
        Assert.Equal(Options.RuntimeImage, created.Image);
        Assert.Equal(512L * 1024 * 1024, created.HostConfig.Memory);
        Assert.Equal(256, created.HostConfig.PidsLimit);
        Assert.Equal(1_000_000_000L, created.HostConfig.NanoCPUs);
        Assert.DoesNotContain(created.HostConfig.Mounts, mount => mount.Type != "volume" && mount.Type != "tmpfs");
        Assert.Equal(Id.ToString(), created.Labels["ctx-ui.builder"]);
    }

    [Fact]
    public void Limits_come_from_the_spec()
    {
        var spec = new ServerSpec(NetworkAccess.InternetOnly, new ServerLimits(1024, 0.5, 64), Secret);
        var created = ContainerTemplate.Server(Id, 1, Hash, spec, NoEnvironment, Options);

        Assert.Equal(1024L * 1024 * 1024, created.HostConfig.Memory);
        Assert.Equal(500_000_000L, created.HostConfig.NanoCPUs);
        Assert.Equal(64, created.HostConfig.PidsLimit);
        Assert.Equal("1024", created.Labels["ctx-ui.builder.memory-mb"]);
        Assert.Equal("0.5", created.Labels["ctx-ui.builder.cpus"]);
        Assert.Equal("64", created.Labels["ctx-ui.builder.pids"]);
    }

    [Fact]
    public void Server_is_named_and_labelled_for_the_runner()
    {
        var created = ContainerTemplate.Server(Id, 3, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);

        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-r3", created.Name);
        Assert.Equal("3", created.Labels["ctx-ui.builder.release"]);
        Assert.Equal(Hash, created.Labels["ctx-ui.builder.environment-hash"]);
        Assert.Equal("InternetOnly", created.Labels["ctx-ui.builder.network"]);
        Assert.Equal(["/env/.venv/bin/fastmcp", "run", "/code/fastmcp.json", "--skip-env", "--no-banner"], created.Cmd);
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-internet", created.HostConfig.NetworkMode);
    }

    [Fact]
    public void Internet_only_server_gets_proxy_and_isolated_gateway()
    {
        var created = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var env = Env(created);
        var proxy = "http://ctx-ui-builder-egress:3128";

        Assert.Equal(proxy, env["HTTPS_PROXY"]);
        Assert.Equal(proxy, env["HTTP_PROXY"]);
        Assert.Equal(proxy, env["https_proxy"]);
        Assert.Equal(proxy, env["http_proxy"]);
        Assert.Equal("127.0.0.1,localhost", env["NO_PROXY"]);
        Assert.Equal("127.0.0.1,localhost", env["no_proxy"]);
        Assert.Equal("true", env["FASTMCP_STATELESS_HTTP"]);
        Assert.Equal("/uv-cache", env["UV_CACHE_DIR"]);

        var network = ContainerTemplate.Network(Id, NetworkAccess.InternetOnly, "10.213.0.0/28");
        Assert.True(network.Internal);
        Assert.Equal("isolated", network.Options["com.docker.network.bridge.gateway_mode_ipv4"]);
        Assert.Equal("10.213.0.0/28", network.IPAM.Config.Single().Subnet);
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-internet", network.Name);
        Assert.Equal(Id.ToString(), network.Labels["ctx-ui.builder"]);
    }

    [Fact]
    public void Full_network_server_has_no_proxy_and_normal_network()
    {
        var created = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.FullNetwork), NoEnvironment, Options);
        var env = Env(created);

        Assert.DoesNotContain(env.Keys, key => key.Contains("proxy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("true", env["FASTMCP_STATELESS_HTTP"]);
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-full", created.HostConfig.NetworkMode);

        var network = ContainerTemplate.Network(Id, NetworkAccess.FullNetwork, "10.213.0.16/28");
        Assert.False(network.Internal);
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-full", network.Name);
        Assert.DoesNotContain("com.docker.network.bridge.gateway_mode_ipv4", network.Options ?? new Dictionary<string, string>());
    }

    [Fact]
    public void Network_name_helper_separates_the_two_accesses()
    {
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-internet", ContainerTemplate.NetworkName(Id, NetworkAccess.InternetOnly));
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-full", ContainerTemplate.NetworkName(Id, NetworkAccess.FullNetwork));
    }

    [Theory]
    [InlineData(NetworkAccess.InternetOnly)]
    [InlineData(NetworkAccess.FullNetwork)]
    public void User_environment_cannot_override_reserved_variables(NetworkAccess access)
    {
        var hostile = new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = "http://evil:1",
            ["HTTP_PROXY"] = "http://evil:1",
            ["NO_PROXY"] = "*",
            ["https_proxy"] = "http://evil:1",
            ["Http_Proxy"] = "http://evil:1",
            ["no_proxy"] = "*",
            ["FASTMCP_STATELESS_HTTP"] = "false",
            ["fastmcp_stateless_http"] = "false",
            ["PATH"] = "/x",
            ["path"] = "/x",
            ["LD_PRELOAD"] = "/x",
            ["ld_preload"] = "/x",
            ["API_KEY"] = "kept"
        };

        var created = ContainerTemplate.Server(Id, 1, Hash, Spec(access), hostile, Options);
        var entries = created.Env.ToList();

        Assert.DoesNotContain(entries, entry => entry.Contains("evil", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("NO_PROXY=*", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("no_proxy=*", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("LD_PRELOAD=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.Equals("FASTMCP_STATELESS_HTTP=false", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("API_KEY=kept", entries);
        Assert.Single(entries, entry => entry.StartsWith("FASTMCP_STATELESS_HTTP=", StringComparison.OrdinalIgnoreCase));
        if (access == NetworkAccess.InternetOnly)
        {
            Assert.Contains("https_proxy=http://ctx-ui-builder-egress:3128", entries);
            Assert.Contains("no_proxy=127.0.0.1,localhost", entries);
        }
    }

    [Fact]
    public void Code_and_environment_volumes_are_read_only_and_data_is_writable()
    {
        var created = ContainerTemplate.Server(Id, 3, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var mounts = created.HostConfig.Mounts.ToDictionary(mount => mount.Target);
        const string prefix = "ctxb-0f8fad5bd9cb469fa16570867728950e";

        Assert.Equal("volume", mounts["/code"].Type);
        Assert.Equal($"{prefix}-code-3", mounts["/code"].Source);
        Assert.True(mounts["/code"].ReadOnly);
        Assert.Equal($"{prefix}-env-0123456789ab", mounts["/env"].Source);
        Assert.True(mounts["/env"].ReadOnly);
        Assert.Equal($"{prefix}-data", mounts["/data"].Source);
        Assert.False(mounts["/data"].ReadOnly);
        Assert.Equal("tmpfs", mounts["/tmp"].Type);
        Assert.DoesNotContain("/uv-cache", mounts.Keys);
    }

    [Fact]
    public void Build_uses_writable_environment_and_read_only_code()
    {
        var created = ContainerTemplate.Build(Id, 3, Hash, Spec(NetworkAccess.InternetOnly), Options);
        var mounts = created.HostConfig.Mounts.ToDictionary(mount => mount.Target);
        const string prefix = "ctxb-0f8fad5bd9cb469fa16570867728950e";

        Assert.Equal($"{prefix}-b3", created.Name);
        Assert.Equal(["python", "/opt/builder/prepare.py"], created.Cmd);
        Assert.Equal("volume", mounts["/code"].Type);
        Assert.True(mounts["/code"].ReadOnly);
        Assert.False(mounts["/env"].ReadOnly);
        Assert.Equal($"{prefix}-env-0123456789ab", mounts["/env"].Source);
        Assert.False(mounts["/uv-cache"].ReadOnly);
        Assert.Equal($"{prefix}-uvcache", mounts["/uv-cache"].Source);
        Assert.Equal("tmpfs", mounts["/tmp"].Type);
        Assert.DoesNotContain("/data", mounts.Keys);
        Assert.Equal("10001", created.User);
        Assert.True(created.HostConfig.ReadonlyRootfs);
        Assert.Equal(["ALL"], created.HostConfig.CapDrop);
        Assert.Contains("no-new-privileges", created.HostConfig.SecurityOpt);
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-internet", created.HostConfig.NetworkMode);
        Assert.Equal("http://ctx-ui-builder-egress:3128", Env(created)["HTTPS_PROXY"]);
        Assert.Equal("/uv-cache", Env(created)["UV_CACHE_DIR"]);
        Assert.Equal(Id.ToString(), created.Labels["ctx-ui.builder"]);
    }

    [Fact]
    public void Build_of_a_full_network_server_has_no_proxy()
    {
        var created = ContainerTemplate.Build(Id, 1, Hash, Spec(NetworkAccess.FullNetwork), Options);

        Assert.DoesNotContain(Env(created).Keys, key => key.Contains("proxy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-full", created.HostConfig.NetworkMode);
    }

    [Fact]
    public void Container_runtime_is_passed_only_from_options()
    {
        var plain = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var sandboxed = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options with { ContainerRuntime = "runsc" });
        var sandboxedBuild = ContainerTemplate.Build(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), Options with { ContainerRuntime = "runsc" });

        Assert.Null(plain.HostConfig.Runtime);
        Assert.Equal("runsc", sandboxed.HostConfig.Runtime);
        Assert.Equal("runsc", sandboxedBuild.HostConfig.Runtime);
    }

    [Fact]
    public void Raw_route_secret_appears_nowhere_and_its_hash_is_a_label()
    {
        var created = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Secret)));

        Assert.Equal(expected, created.Labels["ctx-ui.builder.route-secret-sha256"]);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(created), StringComparison.Ordinal);
    }

    [Fact]
    public void Volumes_and_networks_carry_the_server_label()
    {
        var volume = ContainerTemplate.Volume(Id, ContainerTemplate.DataVolume(Id));
        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-data", volume.Name);
        Assert.Equal(Id.ToString(), volume.Labels["ctx-ui.builder"]);
    }

    [Fact]
    public void Every_volume_mount_carries_the_server_label_and_a_known_name()
    {
        var server = ContainerTemplate.Server(Id, 3, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var build = ContainerTemplate.Build(Id, 3, Hash, Spec(NetworkAccess.InternetOnly), Options);
        var known = new[]
        {
            ContainerTemplate.CodeVolume(Id, 3),
            ContainerTemplate.EnvironmentVolume(Id, Hash),
            ContainerTemplate.CacheVolume(Id),
            ContainerTemplate.DataVolume(Id)
        };

        foreach (var created in new[] { server, build })
        {
            var volumes = created.HostConfig.Mounts.Where(mount => mount.Type == "volume").ToList();
            Assert.NotEmpty(volumes);
            Assert.All(volumes, mount =>
            {
                Assert.Equal(Id.ToString(), mount.VolumeOptions.Labels["ctx-ui.builder"]);
                Assert.Contains(mount.Source, known);
            });
        }
    }

    [Theory]
    [InlineData(0, 1.0, 256)]
    [InlineData(5, 1.0, 256)]
    [InlineData(512, 0.0, 256)]
    [InlineData(512, 0.005, 256)]
    [InlineData(512, double.NaN, 256)]
    [InlineData(512, double.PositiveInfinity, 256)]
    [InlineData(512, 1.0, 0)]
    [InlineData(512, 1.0, -1)]
    public void Limits_that_remove_or_break_a_limit_are_rejected(int memoryMb, double cpus, int pids)
    {
        var spec = new ServerSpec(NetworkAccess.InternetOnly, new ServerLimits(memoryMb, cpus, pids), Secret);

        Assert.Throws<ArgumentOutOfRangeException>(() => ContainerTemplate.Server(Id, 1, Hash, spec, NoEnvironment, Options));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContainerTemplate.Build(Id, 1, Hash, spec, Options));
    }

    [Fact]
    public void Server_restarts_unless_stopped_and_cannot_swap_past_its_memory()
    {
        var created = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);

        Assert.Equal(RestartPolicyKind.UnlessStopped, created.HostConfig.RestartPolicy.Name);
        Assert.Equal(created.HostConfig.Memory, created.HostConfig.MemorySwap);
    }

    [Fact]
    public void Build_has_no_restart_policy_and_cannot_swap_past_its_memory()
    {
        var created = ContainerTemplate.Build(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), Options);

        Assert.Null(created.HostConfig.RestartPolicy);
        Assert.Equal(created.HostConfig.Memory, created.HostConfig.MemorySwap);
    }

    [Fact]
    public void Server_and_build_logs_are_capped()
    {
        var server = ContainerTemplate.Server(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), NoEnvironment, Options);
        var build = ContainerTemplate.Build(Id, 1, Hash, Spec(NetworkAccess.InternetOnly), Options);

        foreach (var created in new[] { server, build })
        {
            Assert.Equal("json-file", created.HostConfig.LogConfig.Type);
            Assert.Equal("10m", created.HostConfig.LogConfig.Config["max-size"]);
            Assert.Equal("3", created.HostConfig.LogConfig.Config["max-file"]);
        }
    }

    [Fact]
    public void Unpack_container_has_no_network_and_a_writable_code_volume_only()
    {
        var created = ContainerTemplate.Unpack(Id, 3, Hash, Options);
        var mounts = created.HostConfig.Mounts.ToDictionary(mount => mount.Target);

        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-u3", created.Name);
        Assert.Equal("none", created.HostConfig.NetworkMode);
        Assert.False(mounts["/code"].ReadOnly);
        Assert.True(mounts["/env"].ReadOnly);
        Assert.Equal(["ALL"], created.HostConfig.CapDrop);
        Assert.True(created.HostConfig.ReadonlyRootfs);
        Assert.Equal("unpack", created.Labels["ctx-ui.builder.role"]);
        Assert.Equal(Id.ToString(), created.Labels["ctx-ui.builder"]);
    }

    [Fact]
    public void Activation_marker_is_a_named_volume_recording_the_active_and_retiring_containers()
    {
        var created = ContainerTemplate.ActivationMarker(Id, 7, 3, "ctxb-a-r3", ["ctxb-a-r2", "ctxb-a-r1"]);

        Assert.Equal("ctxb-0f8fad5bd9cb469fa16570867728950e-active-7", created.Name);
        Assert.Equal(Id.ToString(), created.Labels["ctx-ui.builder"]);
        Assert.Equal("activation", created.Labels["ctx-ui.builder.role"]);
        Assert.Equal("7", created.Labels["ctx-ui.builder.activation"]);
        Assert.Equal("3", created.Labels["ctx-ui.builder.release"]);
        Assert.Equal("ctxb-a-r3", created.Labels["ctx-ui.builder.active-container"]);
        Assert.Equal("ctxb-a-r2,ctxb-a-r1", created.Labels["ctx-ui.builder.retiring-containers"]);
    }

    [Fact]
    public void Minimum_limits_are_accepted()
    {
        var spec = new ServerSpec(NetworkAccess.InternetOnly, new ServerLimits(6, 0.01, 1), Secret);
        var created = ContainerTemplate.Server(Id, 1, Hash, spec, NoEnvironment, Options);

        Assert.Equal(6L * 1024 * 1024, created.HostConfig.Memory);
        Assert.Equal(10_000_000L, created.HostConfig.NanoCPUs);
        Assert.Equal(1, created.HostConfig.PidsLimit);
    }
}
