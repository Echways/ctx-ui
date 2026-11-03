using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Tools;
using Enfilade;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Servers;

public sealed partial class ServerCatalog(BuilderDatabase database, SecretBox box, TimeProvider clock)
{
    private const int RouteSecretBytes = 32;
    private const int MinimumMemoryMb = 6;
    private const double MinimumCpus = 0.01;
    private const string ProjectRoot = "mcp/";
    private const string ServerNameVariable = "BUILDER_SERVER_NAME";
    private const string BaseUrlVariable = "BUILDER_BASE_URL";
    private const string ApiHeadersVariable = "BUILDER_API_HEADERS";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SecretNamePattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderNamePattern();

    private sealed record StoredAuth(ApiAuthKind Kind, string? HeaderName, string? Username, string Secret);

    public Task<BuilderServer> CreateAsync(string slug, string name, CancellationToken ct) => CreateCoreAsync(slug, name, null, ct);

    public Task<BuilderServer> ImportAsync(string slug, string name, ProjectImport import, CancellationToken ct) => CreateCoreAsync(slug, name, import, ct);

    public async Task<BuilderServer> ImportArchiveAsync(string slug, string name, Stream archive, CancellationToken ct)
    {
        ProjectImport import;
        try
        {
            import = await ProjectFiles.ImportArchiveAsync(archive, ct);
        }
        catch (ProjectFileException failure)
        {
            throw new PanelException(failure.Message);
        }

        return await CreateCoreAsync(slug, name, import, ct);
    }

    private async Task<BuilderServer> CreateCoreAsync(string slug, string name, ProjectImport? import, CancellationToken ct)
    {
        if (!SlugPattern().IsMatch(slug))
            throw new PanelException(BuilderText.SlugInvalid);
        if (string.IsNullOrWhiteSpace(name))
            throw new PanelException(BuilderText.NameRequired);
        if (await database.Servers.AnyAsync(server => server.Slug == slug, ct))
            throw new PanelException(BuilderText.SlugTaken);

        if (import is not null && import.Files.FirstOrDefault(file => file.Path == "openapi.json") is { Content: { } document })
        {
            try
            {
                await OpenApiImport.ValidateStoredAsync(document, ct);
            }
            catch (PanelException failure)
            {
                throw new PanelException(string.Format(BuilderText.ArchiveOpenApiInvalid, failure.Message));
            }
        }

        if (import is not null && import.Files.FirstOrDefault(file => file.Path == "builder.json") is { Content: { } builder })
        {
            try
            {
                BuilderDocument.Read(builder);
            }
            catch (PanelException failure)
            {
                throw new PanelException(string.Format(BuilderText.ArchiveBuilderInvalid, failure.Message));
            }
        }

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
        database.Drafts.Add(import is null ? new ServerDraft { ServerId = server.Id, UpdatedAt = now } : DraftOf(server.Id, import, now));
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
        if (!path.StartsWith(ProjectRoot, StringComparison.Ordinal))
            throw new PanelException(BuilderText.PathInvalid);
        path = ProjectPath(path);
        await RequireServerAsync(id, ct);
        await RequireNoFormToolClashAsync(id, path, content, ct);

        var otherBytes = await database.Database
            .SqlQuery<long>($"SELECT COALESCE(SUM(LENGTH(CAST(Content AS BLOB))), 0) AS Value FROM Files WHERE ServerId = {id} AND Path <> {path}")
            .SingleAsync(ct);
        if (otherBytes + Encoding.UTF8.GetByteCount(content) > ProjectFiles.MaxProjectBytes)
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

    public async Task<long> SaveOpenApiAsync(Guid id, string openApiJson, long expectedRevision, CancellationToken ct)
    {
        OpenApiDraft.Parse(openApiJson);
        await RequireServerAsync(id, ct);
        await RequireRoomAsync(id, null, Encoding.UTF8.GetByteCount(openApiJson), ct);
        var now = clock.GetUtcNow();
        var updated = await database.Drafts.Where(draft => draft.ServerId == id && draft.Revision == expectedRevision).ExecuteUpdateAsync(setters => setters
            .SetProperty(draft => draft.OpenApiJson, openApiJson)
            .SetProperty(draft => draft.Revision, expectedRevision + 1)
            .SetProperty(draft => draft.UpdatedAt, now), ct);
        return updated == 0 ? throw new DraftConflictException(BuilderText.DraftChangedElsewhere) : expectedRevision + 1;
    }

    public async Task<long> SaveFormToolAsync(Guid id, FormTool tool, bool isNew, long expectedRevision, CancellationToken ct)
    {
        var draft = await DraftAsync(id, ct);
        if (draft.Revision != expectedRevision)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);

        var document = draft.OpenApiJson ?? string.Empty;
        if (CodeToolNames.From(draft.Files.Select(file => (file.Path, file.Content))).Contains(tool.Name) || (isNew && OpenApiDraft.Names(document).Contains(tool.Name)))
            throw new PanelException(string.Format(BuilderText.ToolNameTaken, tool.Name));
        return await SaveOpenApiAsync(id, OpenApiDraft.Upsert(document, tool), expectedRevision, ct);
    }

    public async Task<long> DeleteFormToolAsync(Guid id, string toolName, long expectedRevision, CancellationToken ct)
    {
        var draft = await DraftAsync(id, ct);
        if (draft.Revision != expectedRevision)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);
        return await SaveOpenApiAsync(id, OpenApiDraft.Remove(draft.OpenApiJson ?? string.Empty, toolName), expectedRevision, ct);
    }

    public async Task<(string Path, long Revision)> ConvertToCodeAsync(Guid id, string toolName, long expectedRevision, CancellationToken ct)
    {
        await RequireServerAsync(id, ct);
        var draft = await database.Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == id, ct);
        if (draft.Revision != expectedRevision)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);

        var (openApiJson, python) = OpenApiDraft.ConvertToCode(draft.OpenApiJson ?? string.Empty, toolName);
        var path = OpenApiDraft.ToolFilePath(toolName);
        await RequireRoomAsync(id, path, Encoding.UTF8.GetByteCount(openApiJson) + Encoding.UTF8.GetByteCount(python), ct);

        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var updated = await database.Drafts.Where(candidate => candidate.ServerId == id && candidate.Revision == expectedRevision).ExecuteUpdateAsync(setters => setters
            .SetProperty(candidate => candidate.OpenApiJson, openApiJson)
            .SetProperty(candidate => candidate.Revision, expectedRevision + 1)
            .SetProperty(candidate => candidate.UpdatedAt, now), ct);
        if (updated == 0)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);

        try
        {
            await InsertFileAsync(id, path, python, ct);
        }
        catch (DraftConflictException)
        {
            throw new PanelException(BuilderText.ToolExists);
        }

        await transaction.CommitAsync(ct);
        return (path, expectedRevision + 1);
    }

    public async Task<long> ImportOperationsAsync(Guid id, ImportedApi api, IReadOnlySet<string> selected, long expectedRevision, CancellationToken ct)
    {
        if (selected.Count == 0)
            throw new PanelException(BuilderText.ImportNothingSelected);
        var draft = await DraftAsync(id, ct);
        if (draft.Revision != expectedRevision)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);

        var reserved = CodeToolNames.From(draft.Files.Select(file => (file.Path, file.Content)));
        var merged = OpenApiDraft.Merge(draft.OpenApiJson ?? string.Empty, OpenApiImport.Select(api, selected), reserved);
        return await SaveOpenApiAsync(id, merged, expectedRevision, ct);
    }

    public Task<long> SaveResourceAsync(Guid id, BuilderResource resource, string? replacing, long expectedRevision, CancellationToken ct) =>
        ChangeBuilderDocumentAsync(id, model => BuilderDocument.WithResource(model, resource, replacing), expectedRevision, ct);

    public Task<long> DeleteResourceAsync(Guid id, string uri, long expectedRevision, CancellationToken ct) =>
        ChangeBuilderDocumentAsync(id, model => BuilderDocument.WithoutResource(model, uri), expectedRevision, ct);

    public Task<long> SavePromptAsync(Guid id, BuilderPrompt prompt, string? replacing, long expectedRevision, CancellationToken ct) =>
        ChangeBuilderDocumentAsync(id, model => BuilderDocument.WithPrompt(model, prompt, replacing), expectedRevision, ct);

    public Task<long> DeletePromptAsync(Guid id, string name, long expectedRevision, CancellationToken ct) =>
        ChangeBuilderDocumentAsync(id, model => BuilderDocument.WithoutPrompt(model, name), expectedRevision, ct);

    public async Task<long> ProposeBaseUrlAsync(Guid id, string baseUrl, CancellationToken ct)
    {
        var settings = await SettingsAsync(id, ct);
        return await SetApiAsync(id, baseUrl, new ApiAuthInput(settings.AuthKind, settings.AuthHeaderName, settings.AuthUsername, null), settings.SettingsRevision, ct);
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

    public async Task<ServerSettingsView> SettingsAsync(Guid id, CancellationToken ct)
    {
        var server = await database.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, ct)
            ?? throw new PanelException(BuilderText.ServerNotFound);
        var draft = await database.Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == id, ct);
        var names = await database.Secrets.AsNoTracking().Where(secret => secret.ServerId == id).Select(secret => secret.Name).OrderBy(name => name).ToListAsync(ct);
        var auth = OpenAuth(draft.ApiAuthSealed);
        return new ServerSettingsView(
            server.Id,
            server.Slug,
            server.Name,
            server.NetworkAccess,
            new ServerLimits(server.MemoryMb, server.CpuLimit, server.PidsLimit),
            draft.BaseUrl,
            auth?.Kind ?? ApiAuthKind.None,
            auth?.HeaderName,
            auth?.Username,
            auth is not null,
            draft.Dependencies ?? string.Empty,
            draft.SettingsRevision,
            names);
    }

    public async Task<long> SetApiAsync(Guid id, string? baseUrl, ApiAuthInput auth, long expectedRevision, CancellationToken ct)
    {
        var uri = ParseBaseUrl(baseUrl);
        await RequireServerAsync(id, ct);
        var sealedAuth = SealAuth(auth, OpenAuth((await database.Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == id, ct)).ApiAuthSealed));
        var address = uri?.ToString().TrimEnd('/');
        var now = clock.GetUtcNow();
        var updated = await database.Drafts.Where(draft => draft.ServerId == id && draft.SettingsRevision == expectedRevision).ExecuteUpdateAsync(setters => setters
            .SetProperty(draft => draft.BaseUrl, address)
            .SetProperty(draft => draft.ApiAuthSealed, sealedAuth)
            .SetProperty(draft => draft.SettingsRevision, expectedRevision + 1)
            .SetProperty(draft => draft.Revision, draft => draft.Revision + 1)
            .SetProperty(draft => draft.UpdatedAt, now), ct);
        return updated == 0 ? throw new DraftConflictException(BuilderText.SettingsChangedElsewhere) : expectedRevision + 1;
    }

    public async Task<long> SetDependenciesAsync(Guid id, string? text, long expectedRevision, CancellationToken ct)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = ProjectFiles.ParseDependencies(text);
        }
        catch (ProjectFileException failure)
        {
            throw new PanelException(failure.Message);
        }

        await RequireServerAsync(id, ct);
        var joined = lines.Count == 0 ? null : string.Join('\n', lines);
        var now = clock.GetUtcNow();
        var updated = await database.Drafts.Where(draft => draft.ServerId == id && draft.SettingsRevision == expectedRevision).ExecuteUpdateAsync(setters => setters
            .SetProperty(draft => draft.Dependencies, joined)
            .SetProperty(draft => draft.SettingsRevision, expectedRevision + 1)
            .SetProperty(draft => draft.Revision, draft => draft.Revision + 1)
            .SetProperty(draft => draft.UpdatedAt, now), ct);
        return updated == 0 ? throw new DraftConflictException(BuilderText.SettingsChangedElsewhere) : expectedRevision + 1;
    }

    public async Task<string> CreateToolAsync(Guid id, string name, CancellationToken ct)
    {
        var path = $"{ProjectRoot}{ProjectFiles.ToolFunctionName(name)}.py";
        try
        {
            await SaveFileAsync(id, path, ProjectFiles.NewToolTemplate(name), 0, ct);
        }
        catch (DraftConflictException)
        {
            throw new PanelException(BuilderText.ToolExists);
        }

        return path;
    }

    public async Task<string> RouteSecretAsync(Guid id, CancellationToken ct)
    {
        var server = await database.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, ct)
            ?? throw new PanelException(BuilderText.ServerNotFound);
        return box.Open(server.RouteSecretSealed);
    }

    public async Task<ServerLaunch> LaunchAsync(Guid id, string? baseUrl, CancellationToken ct)
    {
        var server = await database.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, ct)
            ?? throw new PanelException(BuilderText.ServerNotFound);
        var draft = await database.Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == id, ct);
        var secrets = await database.Secrets.AsNoTracking().Where(secret => secret.ServerId == id).ToListAsync(ct);

        var environment = secrets.ToDictionary(secret => secret.Name, secret => box.Open(secret.ValueSealed));
        environment[ServerNameVariable] = server.Name;
        environment.Remove(BaseUrlVariable);
        environment.Remove(ApiHeadersVariable);
        if (!string.IsNullOrEmpty(baseUrl))
            environment[BaseUrlVariable] = baseUrl;
        if (OpenAuth(draft.ApiAuthSealed) is { } auth)
            environment[ApiHeadersVariable] = JsonSerializer.Serialize(new Dictionary<string, string> { [auth.Kind == ApiAuthKind.Header ? auth.HeaderName! : "Authorization"] = AuthValue(auth) });

        var spec = new ServerSpec(server.NetworkAccess, new ServerLimits(server.MemoryMb, server.CpuLimit, server.PidsLimit), box.Open(server.RouteSecretSealed));
        return new ServerLaunch(spec, new StartRelease(environment));
    }

    public async Task<ProjectExport> ExportAsync(Guid id, CancellationToken ct)
    {
        var server = await database.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, ct)
            ?? throw new PanelException(BuilderText.ServerNotFound);
        var draft = await DraftAsync(id, ct);
        try
        {
            return new ProjectExport($"{server.Slug}.tar.gz", await ProjectFiles.SnapshotAsync(draft, draft.Files, ProjectFiles.FastMcpVersion, ct));
        }
        catch (ProjectFileException failure)
        {
            throw new PanelException(failure.Message);
        }
    }

    private static string AuthValue(StoredAuth auth) => auth.Kind switch
    {
        ApiAuthKind.Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Secret}")),
        ApiAuthKind.Bearer => $"Bearer {auth.Secret}",
        _ => auth.Secret,
    };

    private StoredAuth? OpenAuth(string? sealedAuth) =>
        sealedAuth is null ? null : JsonSerializer.Deserialize<StoredAuth>(box.Open(sealedAuth));

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

    private async Task<long> ChangeBuilderDocumentAsync(Guid id, Func<BuilderDocumentModel, BuilderDocumentModel> change, long expectedRevision, CancellationToken ct)
    {
        await RequireServerAsync(id, ct);
        var draft = await database.Drafts.AsNoTracking().SingleAsync(draft => draft.ServerId == id, ct);
        if (draft.Revision != expectedRevision)
            throw new DraftConflictException(BuilderText.DraftChangedElsewhere);

        var model = change(BuilderDocument.Read(draft.BuilderJson));
        var json = model.IsEmpty ? null : BuilderDocument.Write(model);
        await RequireRoomAsync(id, null, Encoding.UTF8.GetByteCount(json ?? string.Empty) + Encoding.UTF8.GetByteCount(draft.OpenApiJson ?? string.Empty), ct);

        var now = clock.GetUtcNow();
        var updated = await database.Drafts.Where(candidate => candidate.ServerId == id && candidate.Revision == expectedRevision).ExecuteUpdateAsync(setters => setters
            .SetProperty(candidate => candidate.BuilderJson, json)
            .SetProperty(candidate => candidate.Revision, expectedRevision + 1)
            .SetProperty(candidate => candidate.UpdatedAt, now), ct);
        return updated == 0 ? throw new DraftConflictException(BuilderText.DraftChangedElsewhere) : expectedRevision + 1;
    }

    private async Task RequireNoFormToolClashAsync(Guid id, string path, string content, CancellationToken ct)
    {
        if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return;
        var names = CodeToolNames.From([(path, content)]).ToHashSet();
        if (names.Count == 0)
            return;

        var previous = await database.Files.AsNoTracking().Where(file => file.ServerId == id && file.Path == path).Select(file => file.Content).SingleOrDefaultAsync(ct);
        if (previous is not null)
            names.ExceptWith(CodeToolNames.From([(path, previous)]));
        var document = await database.Drafts.AsNoTracking().Where(draft => draft.ServerId == id).Select(draft => draft.OpenApiJson).SingleOrDefaultAsync(ct);
        if (names.Count == 0 || document is null)
            return;

        IReadOnlySet<string> formNames;
        try
        {
            formNames = OpenApiDraft.Names(document);
        }
        catch (PanelException)
        {
            return;
        }

        if (names.FirstOrDefault(formNames.Contains) is { } clash)
            throw new PanelException(string.Format(BuilderText.CodeToolNameTaken, clash));
    }

    private async Task RequireRoomAsync(Guid id, string? replacedPath, long addedBytes, CancellationToken ct)
    {
        var otherBytes = await database.Database
            .SqlQuery<long>($"SELECT COALESCE(SUM(LENGTH(CAST(Content AS BLOB))), 0) AS Value FROM Files WHERE ServerId = {id} AND Path <> {replacedPath ?? string.Empty}")
            .SingleAsync(ct);
        if (otherBytes + addedBytes > ProjectFiles.MaxProjectBytes)
            throw new PanelException(BuilderText.ProjectTooLarge);
    }

    private async Task RequireServerAsync(Guid id, CancellationToken ct)
    {
        if (!await database.Servers.AnyAsync(server => server.Id == id, ct))
            throw new PanelException(BuilderText.ServerNotFound);
    }

    private static string ProjectPath(string path)
    {
        try
        {
            return ProjectFiles.ValidatePath(path);
        }
        catch (ProjectFileException failure)
        {
            throw new PanelException(failure.Message);
        }
    }

    private static ServerDraft DraftOf(Guid id, ProjectImport import, DateTimeOffset now)
    {
        var draft = new ServerDraft { ServerId = id, UpdatedAt = now, Revision = 1, Dependencies = import.Dependencies };
        long bytes = 0;
        foreach (var (path, content) in import.Files)
        {
            bytes += Encoding.UTF8.GetByteCount(content);
            switch (ProjectPath(path))
            {
                case "openapi.json":
                    draft.OpenApiJson = content;
                    break;
                case "builder.json":
                    draft.BuilderJson = content;
                    break;
                default:
                    draft.Files.Add(new DraftFile { ServerId = id, Path = path, Content = content, Revision = 1 });
                    break;
            }
        }

        if (bytes > ProjectFiles.MaxProjectBytes)
            throw new PanelException(BuilderText.ProjectTooLarge);
        return draft;
    }

    private static Uri? ParseBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PanelException(BuilderText.BaseUrlInvalid);
        return uri;
    }

    private string? SealAuth(ApiAuthInput input, StoredAuth? existing)
    {
        if (!Enum.IsDefined(input.Kind))
            throw new PanelException(BuilderText.AccessInvalid);
        if (input.Kind == ApiAuthKind.None)
            return null;
        if (input.Kind == ApiAuthKind.Header && !HeaderNamePattern().IsMatch(input.HeaderName ?? string.Empty))
            throw new PanelException(BuilderText.AuthHeaderInvalid);
        if (input.Kind == ApiAuthKind.Basic && (string.IsNullOrWhiteSpace(input.Username) || input.Username.Contains(':')))
            throw new PanelException(BuilderText.AuthUsernameInvalid);

        var secret = !string.IsNullOrEmpty(input.Secret) ? input.Secret : existing?.Kind == input.Kind ? existing.Secret : null;
        if (string.IsNullOrEmpty(secret))
            throw new PanelException(BuilderText.SecretValueRequired);

        var stored = new StoredAuth(input.Kind, input.Kind == ApiAuthKind.Header ? input.HeaderName : null, input.Kind == ApiAuthKind.Basic ? input.Username?.Trim() : null, secret);
        return box.Seal(JsonSerializer.Serialize(stored));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
