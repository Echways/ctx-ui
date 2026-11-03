using System.Net;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Extensions;
using Enfilade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CtxUi.Builder.Servers;

public sealed class ConnectionService(
    BuilderDatabase database,
    IPanelData data,
    RunnerClient runner,
    RunnerSettings settings,
    SecretBox box,
    ReleaseArchives archives,
    PublishQueue queue,
    ConnectedInbounds connected,
    ILogger<ConnectionService> log)
{
    public async Task<InboundSummary> ConnectAsync(Guid serverId, string slug, string name, bool forwardIdentity, IReadOnlyCollection<Guid> clients, CancellationToken ct)
    {
        RequireConfigured();
        queue.ReserveForConnection(serverId);
        try
        {
            return await ConnectCoreAsync(serverId, slug, name, forwardIdentity, clients, ct);
        }
        finally
        {
            queue.ReleaseConnection(serverId);
        }
    }

    public async Task<InboundSummary?> FindInboundAsync(BuilderServer server, CancellationToken ct)
    {
        var inbound = OwnInbound(server, await data.InboundsAsync(ct));
        if (inbound is not null)
            connected.Add(inbound.Id);
        else if (Guid.TryParse(server.InboundId, out var stale))
            connected.Remove(stale);
        return inbound;
    }

    public async Task<InboundSummary?> FindInboundAsync(Guid serverId, CancellationToken ct) =>
        await FindInboundAsync(await FindServerAsync(serverId, ct), ct);

    public async Task<Guid?> FindServerIdAsync(InboundSummary inbound, CancellationToken ct)
    {
        var id = inbound.Id.ToString();
        var found = await database.Servers.AsNoTracking().Where(server => server.InboundId == id).Select(server => (Guid?)server.Id).FirstOrDefaultAsync(ct);
        if (found is null)
            connected.Remove(inbound.Id);
        return found;
    }

    public async Task DeleteServerAsync(Guid serverId, CancellationToken ct)
    {
        RequireConfigured();
        var server = await FindServerAsync(serverId, ct);
        queue.ReserveForDeletion(serverId);
        try
        {
            await DeleteFromRunnerAsync(serverId, ct);
            if (await FindInboundAsync(server, ct) is { } inbound)
            {
                await data.RemoveInboundAsync(inbound.Slug, ct);
                connected.Remove(inbound.Id);
            }

            var releases = await database.Releases.AsNoTracking().Where(release => release.ServerId == serverId).Select(release => release.Id).ToListAsync(ct);
            await database.Servers.Where(known => known.Id == serverId).ExecuteDeleteAsync(ct);
            RemoveArchives(releases);
        }
        finally
        {
            queue.ReleaseDeletion(serverId);
        }
    }

    public string? AddressOf(Guid serverId) =>
        settings.IngressUrl is null ? null : $"{settings.IngressUrl.ToString().TrimEnd('/')}/s/{serverId}/mcp";

    private async Task<InboundSummary> ConnectCoreAsync(Guid serverId, string slug, string name, bool forwardIdentity, IReadOnlyCollection<Guid> clients, CancellationToken ct)
    {
        var server = await FindServerAsync(serverId, ct);
        var inbounds = await data.InboundsAsync(ct);
        if (OwnInbound(server, inbounds) is not null)
            throw new PanelException(BuilderText.ConnectionAlreadyConnected);

        var wanted = slug.Trim();
        if (inbounds.Any(inbound => inbound.Slug == wanted))
            throw new PanelException(BuilderText.InboundSlugTaken);

        var inbound = await AddInboundAsync(new HttpInboundDraft(wanted, name.Trim(), AddressOf(serverId)!, new Dictionary<string, string> { [RouteSecret.Header] = box.Open(server.RouteSecretSealed) }, forwardIdentity), ct);
        try
        {
            if (clients.Count > 0)
                await data.SetInboundClientsAsync(inbound.Slug, clients, ct);
            var previous = server.InboundId;
            var updated = await database.Servers.Where(known => known.Id == serverId && known.InboundId == previous)
                .ExecuteUpdateAsync(setters => setters.SetProperty(known => known.InboundId, inbound.Id.ToString()), ct);
            if (updated == 0)
                throw new PanelException(await database.Servers.AnyAsync(known => known.Id == serverId, ct) ? BuilderText.ConnectionAlreadyConnected : BuilderText.ServerNotFound);
        }
        catch
        {
            await data.RemoveInboundAsync(inbound.Slug, CancellationToken.None);
            throw;
        }

        connected.Add(inbound.Id);
        return inbound;
    }

    private async Task<InboundSummary> AddInboundAsync(HttpInboundDraft draft, CancellationToken ct)
    {
        try
        {
            return await data.AddHttpInboundAsync(draft, ct);
        }
        catch (PanelException failure)
        {
            throw new PanelException(string.Format(BuilderText.InboundRejected, failure.Message));
        }
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

    private void RemoveArchives(IEnumerable<Guid> releases)
    {
        foreach (var release in releases)
        {
            try
            {
                archives.Remove(release);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(failure, "The archive of release {Release} could not be removed.", release);
            }
        }
    }

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
}
