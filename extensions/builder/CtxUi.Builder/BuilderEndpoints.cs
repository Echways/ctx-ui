using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CtxUi.Builder;

public static class BuilderEndpoints
{
    private const string ArchiveContentType = "application/gzip";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(string.Empty);
        group.AddEndpointFilter(TranslateDomainErrorsAsync);
        group.MapGet("/servers", ListAsync);
        group.MapGet("/servers/{id:guid}", DetailsAsync);
        group.MapPost("/servers/{id:guid}/publish", PublishAsync);
        group.MapPost("/servers/{id:guid}/rollback/{number:int}", RollbackAsync);
        group.MapGet("/servers/{id:guid}/export", ExportAsync);
    }

    private static async Task<IResult> ListAsync(ServerCatalog catalog, ReleaseCatalog releases, CancellationToken ct)
    {
        var servers = await catalog.ListAsync(ct);
        var statuses = await Task.WhenAll(servers.Select(server => releases.StatusAsync(server.Id, ct)));
        return Results.Ok(servers.Zip(statuses, (server, status) => new ServerSummaryModel(
            server.Id,
            server.Slug,
            server.Name,
            server.NetworkAccess.ToString(),
            status.State.ToString(),
            status.ActiveNumber,
            status.BuildingNumber,
            server.InboundId)));
    }

    private static async Task<IResult> DetailsAsync(Guid id, ServerCatalog catalog, ReleaseCatalog releases, CancellationToken ct)
    {
        var settings = await catalog.SettingsAsync(id, ct);
        var status = await releases.StatusAsync(id, ct);
        var views = await releases.ListAsync(id, ct);
        return Results.Ok(new ServerDetailsModel(
            settings.ServerId,
            settings.Slug,
            settings.Name,
            settings.Access.ToString(),
            settings.Limits,
            settings.BaseUrl,
            status.State.ToString(),
            status.ActiveNumber,
            status.BuildingNumber,
            settings.SecretNames,
            [.. views.Select(view => new ReleaseModel(view.Number, view.Status.ToString(), view.CreatedAt, view.FinishedAt, view.IsActive, view.CanRollback, view.Error, view.Tools, view.RemovedTools))]));
    }

    private static async Task<IResult> PublishAsync(Guid id, PublishQueue queue, RunnerSettings settings, CancellationToken ct, PublishMode mode = PublishMode.Activate)
    {
        if (!settings.IsComplete)
            return RunnerNotConfigured(settings);
        return Results.Accepted(null, new ReleaseStartedModel(await queue.PublishAsync(id, mode, ct)));
    }

    private static async Task<IResult> RollbackAsync(Guid id, int number, PublishQueue queue, RunnerSettings settings, CancellationToken ct)
    {
        if (!settings.IsComplete)
            return RunnerNotConfigured(settings);
        await queue.RollbackAsync(id, number, ct);
        return Results.Accepted(null, new ReleaseStartedModel(number));
    }

    private static async Task<IResult> ExportAsync(Guid id, ServerCatalog catalog, CancellationToken ct)
    {
        var export = await catalog.ExportAsync(id, ct);
        return Results.File(export.Content, ArchiveContentType, export.FileName);
    }

    private static IResult RunnerNotConfigured(RunnerSettings settings) =>
        Results.Problem(string.Format(BuilderText.RunnerNotConfigured, string.Join(", ", settings.Missing)), statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async ValueTask<object?> TranslateDomainErrorsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (PanelException failure)
        {
            return Results.Problem(failure.Message, statusCode: StatusOf(failure.Message));
        }
        catch (DraftConflictException failure)
        {
            return Results.Problem(failure.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ProjectFileException failure)
        {
            return Results.Problem(failure.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (RunnerException failure)
        {
            return Results.Problem(failure.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static int StatusOf(string message)
    {
        if (message == BuilderText.ServerNotFound || message == BuilderText.ReleaseNotFound)
            return StatusCodes.Status404NotFound;
        if (message == BuilderText.PublishBusy || message == BuilderText.ServerDeleting || message == BuilderText.RollbackUnavailable)
            return StatusCodes.Status409Conflict;
        return StatusCodes.Status400BadRequest;
    }
}
