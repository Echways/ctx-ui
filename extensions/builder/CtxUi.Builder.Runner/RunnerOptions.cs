using System.Net;
using System.Net.Sockets;

namespace CtxUi.Builder.Runner;

public sealed record RunnerOptions(
    string Token,
    string BindAddress,
    string RuntimeImage,
    string EgressContainer,
    string SubnetPool,
    string? ContainerRuntime,
    string SelfContainer = "")
{
    private const string DefaultSubnetPool = "10.213.0.0/16";

    public static RunnerOptions FromEnvironment(IReadOnlyDictionary<string, string?> variables)
    {
        string Required(string name)
        {
            return variables.GetValueOrDefault(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"The environment variable {name} is required.");
        }

        var bindAddress = Required("RUNNER_BIND");
        if (!IPAddress.TryParse(bindAddress, out var parsed)
            || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.IPv6Any)
            || parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new InvalidOperationException(
                $"RUNNER_BIND must be the address of one interface, got '{bindAddress}'.");

        return new RunnerOptions(
            Required("RUNNER_TOKEN"),
            bindAddress,
            Required("RUNNER_RUNTIME_IMAGE"),
            Required("RUNNER_EGRESS_CONTAINER"),
            variables.GetValueOrDefault("RUNNER_SUBNET_POOL") is { Length: > 0 } pool ? pool : DefaultSubnetPool,
            variables.GetValueOrDefault("BUILDER_RUNTIME") is { Length: > 0 } runtime ? runtime : null,
            Required("HOSTNAME"));
    }
}
