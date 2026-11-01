using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using Enfilade;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Servers;

public sealed partial class ServerCatalog(BuilderDatabase database, SecretBox box, TimeProvider clock)
{
    private const int MaximumProjectBytes = 16 * 1024 * 1024;
    private const int RouteSecretBytes = 32;
    private const int MinimumMemoryMb = 6;
    private const double MinimumCpus = 0.01;
    private const string ProjectRoot = "mcp/";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SecretNamePattern();

    public async Task<BuilderServer> CreateAsync(string slug, string name, CancellationToken ct)
    {
        if (!SlugPattern().IsMatch(slug))
            throw new PanelException(BuilderText.SlugInvalid);
        if (string.IsNullOrWhiteSpace(name))
            throw new PanelException(BuilderText.NameRequired);
        if (await database.Servers.AnyAsync(server => server.Slug == slug, ct))
            throw new PanelException(BuilderText.SlugTaken);

        var limits = new ServerLimits();
        var now = clock.GetUtcNow();
        var server = new BuilderServer
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name.Trim(),
            NetworkAccess = NetworkAccess.InternetOnly,
            MemoryMb = limits.MemoryMb,
            CpuLimit = limits.Cpus,
            PidsLimit = limits.Pids,
            RouteSecretSealed = box.Seal(Base64Url(RandomNumberGenerator.GetBytes(RouteSecretBytes))),
            CreatedAt = now,
        };
        database.Servers.Add(server);
        database.Drafts.Add(new ServerDraft { ServerId = server.Id, UpdatedAt = now });
        try
        {
            await database.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            if (await database.Servers.AnyAsync(other => other.Slug == slug, ct))
                throw new PanelException(BuilderText.SlugTaken);
            throw;
        }

        database.ChangeTracker.Clear();

        return server;
    }

    public async Task<IReadOnlyList<BuilderServer>> ListAsync(CancellationToken ct) =>
        await database.Servers.AsNoTracking().OrderBy(server => server.Slug).ToListAsync(ct);

    public async Task<ServerDraft> DraftAsync(Guid id, CancellationToken ct) =>
        await database.Drafts.AsNoTracking().Include(draft => draft.Files).SingleOrDefaultAsync(draft => draft.ServerId == id, ct)
        ?? throw new PanelException(BuilderText.ServerNotFound);

    public async Task<long> SaveFileAsync(Guid id, string path, string content, long expectedRevision, CancellationToken ct)
    {
        if (!IsProjectPath(path))
            throw new PanelException(BuilderText.PathInvalid);
        await RequireServerAsync(id, ct);

        var otherBytes = await database.Database
            .SqlQuery<long>($"SELECT COALESCE(SUM(LENGTH(CAST(Content AS BLOB))), 0) AS Value FROM Files WHERE ServerId = {id} AND Path <> {path}")
            .SingleAsync(ct);
        if (otherBytes + Encoding.UTF8.GetByteCount(content) > MaximumProjectBytes)
            throw new PanelException(BuilderText.ProjectTooLarge);

        var revision = expectedRevision + 1;
        if (expectedRevision == 0)
            await InsertFileAsync(id, path, content, ct);
        else
        {
            var updated = await database.Files
                .Where(file => file.ServerId == id && file.Path == path && file.Revision == expectedRevision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.Content, content).SetProperty(file => file.Revision, revision), ct);
            if (updated == 0)
                throw new DraftConflictException(BuilderText.DraftChangedElsewhere);
        }

        var now = clock.GetUtcNow();
        await database.Drafts.Where(draft => draft.ServerId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(draft => draft.Revision, draft => draft.Revision + 1).SetProperty(draft => draft.UpdatedAt, now), ct);
        return revision;
    }

    public async Task SetSecretAsync(Guid id, string name, string value, CancellationToken ct)
    {
        if (!SecretNamePattern().IsMatch(name))
            throw new PanelException(BuilderText.SecretNameInvalid);
        if (string.IsNullOrEmpty(value))
            throw new PanelException(BuilderText.SecretValueRequired);
        await RequireServerAsync(id, ct);

        var sealedValue = box.Seal(value);
        var updated = await database.Secrets
            .Where(secret => secret.ServerId == id && secret.Name == name)
            .ExecuteUpdateAsync(setters => setters.SetProperty(secret => secret.ValueSealed, sealedValue), ct);
        if (updated > 0)
            return;

        database.Secrets.Add(new ServerSecret { ServerId = id, Name = name, ValueSealed = sealedValue });
        await database.SaveChangesAsync(ct);
        database.ChangeTracker.Clear();
    }

    public async Task SetAccessAsync(Guid id, NetworkAccess access, ServerLimits limits, CancellationToken ct)
    {
        if (!Enum.IsDefined(access))
            throw new PanelException(BuilderText.AccessInvalid);
        if (limits.MemoryMb < MinimumMemoryMb || !double.IsFinite(limits.Cpus) || limits.Cpus < MinimumCpus || limits.Pids < 1)
            throw new PanelException(BuilderText.LimitsInvalid);

        await RequireServerAsync(id, ct);
        await database.Servers.Where(server => server.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(server => server.NetworkAccess, access)
            .SetProperty(server => server.MemoryMb, limits.MemoryMb)
            .SetProperty(server => server.CpuLimit, limits.Cpus)
            .SetProperty(server => server.PidsLimit, limits.Pids), ct);
    }

    private async Task InsertFileAsync(Guid id, string path, string content, CancellationToken ct)
    {
        database.Files.Add(new DraftFile { ServerId = id, Path = path, Content = content, Revision = 1 });
        try
        {
            await database.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private async Task RequireServerAsync(Guid id, CancellationToken ct)
    {
        if (!await database.Servers.AnyAsync(server => server.Id == id, ct))
            throw new PanelException(BuilderText.ServerNotFound);
    }

    private static bool IsProjectPath(string path)
    {
        if (!path.StartsWith(ProjectRoot, StringComparison.Ordinal) || path.Length > 512 || path.Any(char.IsControl) || path.Contains('\\'))
            return false;
        return path[ProjectRoot.Length..].Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
