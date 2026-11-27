using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Settings;

public sealed record HarnessProfile(string Id, string Name, bool Enabled, string? PublicUrl);

public sealed class PublicEndpointSettings(CtxUiDbContext db, IOptions<PublicEndpointOptions> options)
{
    private const string GlobalKey = "public.url";
    private const string HarnessPrefix = "public.harness.";

    private static readonly HarnessProfile[] BuiltIn =
    [
        new("chatgpt-web", "ChatGPT Web", true, null),
        new("claude", "Claude", true, null),
        new("cursor", "Cursor", true, null)
    ];

    public async Task<PublicEndpoints> GetAsync(CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(setting => setting.Key == GlobalKey || setting.Key.StartsWith(HarnessPrefix))
            .ToListAsync(ct);
        var global = rows.SingleOrDefault(setting => setting.Key == GlobalKey)?.Value;
        var profiles = BuiltIn.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var row in rows.Where(setting => setting.Key != GlobalKey))
        {
            var profile = JsonSerializer.Deserialize<HarnessProfile>(row.Value)!;
            profiles[profile.Id] = profile;
        }

        return new PublicEndpoints(global ?? options.Value.PublicUrl, profiles.Values, rows.Count == 0);
    }

    public Task SaveGlobalAsync(string? url, CancellationToken ct = default)
    {
        return SaveAsync(GlobalKey, ValidUrl(url), ct);
    }

    public Task SaveProfileAsync(HarnessProfile profile, CancellationToken ct = default)
    {
        return SaveProfileAsync(profile, false, ct);
    }

    public Task CreateProfileAsync(HarnessProfile profile, CancellationToken ct = default)
    {
        return SaveProfileAsync(profile, true, ct);
    }

    public Task ResetProfileUrlAsync(string id, CancellationToken ct = default)
    {
        var key = HarnessPrefix + Slug.Parse(id);
        return ChangeAsync(async () =>
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "Settings" SET "Value" = json_set("Value", '$.PublicUrl', NULL)
                 WHERE "Key" = {key}
                 """, ct);
        }, ct);
    }

    private async Task SaveProfileAsync(HarnessProfile profile, bool create, CancellationToken ct)
    {
        var id = Slug.Parse(profile.Id);
        if (id == PublicEndpoints.DefaultId)
            throw new DomainException(CoreText.DefaultHarnessFixed);

        if (create && BuiltIn.Any(profile => profile.Id == id))
            throw new DomainException(CoreText.HarnessAlreadyExists);

        var name = profile.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new DomainException(CoreText.HarnessNameInvalid);

        var url = string.IsNullOrWhiteSpace(profile.PublicUrl) ? null : ValidUrl(profile.PublicUrl);
        if (url is not null && PublicAddress.Parse(options.Value.PublicUrl) is { } initial &&
            PublicAddress.PathOf(options.Value.PublicUrl).Trim('/').Length > 0 &&
            url == PublicAddress.Authority(initial) && !await db.Settings.AnyAsync(setting => setting.Key == GlobalKey, ct))
            throw new DomainException(CoreText.LegacyPublicPathConflict);

        await SaveAsync(HarnessPrefix + id,
            JsonSerializer.Serialize(new HarnessProfile(id, name, profile.Enabled, url)), ct, create);
    }

    private static string ValidUrl(string? value)
    {
        return PublicAddress.Normalize(value) ?? throw new DomainException(CoreText.PublicUrlInvalid);
    }

    private Task SaveAsync(string key, string value, CancellationToken ct, bool create = false)
    {
        return ChangeAsync(async () =>
        {
            var changed = await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO "Settings" ("Key", "Value") VALUES ({key}, {value})
                 ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value" WHERE {!create}
                 """, ct);
            if (changed == 0)
                throw new DomainException(CoreText.HarnessAlreadyExists);
        }, ct);
    }

    private async Task ChangeAsync(Func<Task> change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await change();
        await db.RoutingStates.ExecuteUpdateAsync(
            setters => setters.SetProperty(state => state.Revision, state => state.Revision + 1), ct);
        await transaction.CommitAsync(ct);
    }
}
