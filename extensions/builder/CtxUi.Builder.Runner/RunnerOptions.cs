namespace CtxUi.Builder.Runner;

public sealed record RunnerOptions(
    string Token,
    string BindAddress,
    string RuntimeImage,
    string EgressContainer,
    string SubnetPool,
    string? ContainerRuntime);
