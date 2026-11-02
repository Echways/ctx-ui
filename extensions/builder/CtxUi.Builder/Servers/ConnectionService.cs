using System.Net;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Extensions;
using Enfilade;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Servers;

public sealed class ConnectionService(
    BuilderDatabase database,
    IPanelData data,
    RunnerClient runner,
    RunnerSettings settings,
    SecretBox box,
    ReleaseArchives archives,
    PublishQueue queue)
{
    private const string DomainFailureType = "CtxUi.Core.DomainException";

    public async Task<InboundSummary> ConnectAsync(Guid serverId, string slug, string name, bool forwardIdentity, IReadOnlyCollection<Guid> clients, CancellationToken ct)
    {
        RequireConfigured();
        var server = await FindServerAsync(serverId, ct);
        var inbounds = await data.InboundsAsync(ct);
        if (OwnInbound(server, inbounds) is not null)
            throw new PanelException(BuilderText.ConnectionAlreadyConnected);

        var wanted = slug.Trim();
        if (inbounds.Any(inbound => inbound.Slug == wanted))
            throw new PanelException(BuilderText.InboundSlugTaken);

        var headers = new Dictionary<string, string> { [RouteSecret.Header] = box.Open(server.RouteSecretSealed) };
        var inbound = await GuardAsync(() => data.AddHttpInboundAsync(new HttpInboundDraft(wanted, name.Trim(), AddressOf(serverId)!, headers, forwardIdentity), ct));
        try
        {
            if (clients.Count > 0)
                await GuardAsync(() => data.SetInboundClientsAsync(inbound.Slug, clients, ct));
            await database.Servers.Where(known => known.Id == serverId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(known => known.InboundId, inbound.Id.ToString()), ct);
        }
        catch
        {
            await data.RemoveInboundAsync(inbound.Slug, CancellationToken.None);
            throw;
        }

        return inbound;
    }

    public async Task<InboundSummary?> FindInboundAsync(BuilderServer server, CancellationToken ct) =>
        OwnInbound(server, await data.InboundsAsync(ct));

    public async Task<InboundSummary?> FindInboundAsync(Guid serverId, CancellationToken ct) =>
        await FindInboundAsync(await FindServerAsync(serverId, ct), ct);

    public async Task<Guid?> FindServerIdAsync(InboundSummary inbound, CancellationToken ct)
    {
        var id = inbound.Id.ToString();
        return await database.Servers.AsNoTracking().Where(server => server.InboundId == id).Select(server => (Guid?)server.Id).FirstOrDefaultAsync(ct);
    }

    public async Task DeleteServerAsync(Guid serverId, CancellationToken ct)
    {
        RequireConfigured();
        var server = await FindServerAsync(serverId, ct);
        if (queue.IsBusy(serverId))
            throw new PanelException(BuilderText.PublishBusy);

        await DeleteFromRunnerAsync(serverId, ct);
        if (await FindInboundAsync(server, ct) is { } inbound)
            await data.RemoveInboundAsync(inbound.Slug, ct);

        var releases = await database.Releases.AsNoTracking().Where(release => release.ServerId == serverId).Select(release => release.Id).ToListAsync(ct);
        await database.Servers.Where(known => known.Id == serverId).ExecuteDeleteAsync(ct);
        foreach (var release in releases)
            archives.Remove(release);
    }

    private async Task DeleteFromRunnerAsync(Guid serverId, CancellationToken ct)
    {
        try
        {
            await runner.DeleteServerAsync(serverId, ct);
        }
        catch (RunnerException failure) when (failure.Status == HttpStatusCode.NotFound)
        {
        }
        catch (RunnerException failure)
        {
            throw new PanelException(string.Format(BuilderText.RunnerDeleteFailed, failure.Message));
        }
    }

    public string? AddressOf(Guid serverId) =>
        settings.IngressUrl is null ? null : $"{settings.IngressUrl.ToString().TrimEnd('/')}/s/{serverId}/mcp";

    private static InboundSummary? OwnInbound(BuilderServer server, IReadOnlyList<InboundSummary> inbounds) =>
        server.InboundId is null ? null : inbounds.FirstOrDefault(inbound => inbound.Id.ToString() == server.InboundId);

    private async Task<BuilderServer> FindServerAsync(Guid serverId, CancellationToken ct) =>
        await database.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == serverId, ct)
        ?? throw new PanelException(BuilderText.ServerNotFound);

    private void RequireConfigured()
    {
        if (!settings.IsComplete)
            throw new PanelException(string.Format(BuilderText.RunnerNotConfigured, string.Join(", ", settings.Missing)));
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception failure) when (IsDomainFailure(failure))
        {
            throw new PanelException(string.Format(BuilderText.InboundRejected, failure.Message));
        }
    }

    private static async Task GuardAsync(Func<Task> call) => await GuardAsync(async () =>
    {
        await call();
        return true;
    });

    private static bool IsDomainFailure(Exception failure)
    {
        for (var type = failure.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == DomainFailureType)
                return true;
        }

        return false;
    }
}
