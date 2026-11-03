using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public sealed class ServerNetworks(IDockerEngine engine, RunnerOptions options)
{
    private readonly SemaphoreSlim _allocation = new(1, 1);

    public async Task EnsureAsync(Guid id, NetworkAccess access, CancellationToken ct)
    {
        var name = ContainerTemplate.NetworkName(id, access);
        await _allocation.WaitAsync(ct);
        try
        {
            if (!await engine.NetworkExistsAsync(id, name, ct))
            {
                var subnet = SubnetAllocator.Next(options.SubnetPool, await engine.ListSubnetsAsync(ct));
                await engine.CreateNetworkAsync(id, ContainerTemplate.Network(id, access, subnet), ct);
            }
        }
        finally
        {
            _allocation.Release();
        }

        await AttachAsync(id, access, ct);
    }

    public async Task AttachAsync(Guid id, NetworkAccess access, CancellationToken ct)
    {
        var name = ContainerTemplate.NetworkName(id, access);
        await engine.ConnectAsync(id, name, options.SelfContainer, ct);
        if (access == NetworkAccess.InternetOnly)
        {
            await engine.ConnectAsync(id, name, options.EgressContainer, ct);
        }
    }

    public async Task RemoveAsync(Guid id, string network, CancellationToken ct)
    {
        await engine.DisconnectAsync(id, network, options.SelfContainer, ct);
        await engine.DisconnectAsync(id, network, options.EgressContainer, ct);
        await engine.RemoveNetworkAsync(id, network, ct);
    }
}
