using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CtxUi.Builder.Contracts;
using Docker.DotNet;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace CtxUi.Builder.Runner;

public sealed record RunnerPorts(int Ingress, int Control)
{
    public static readonly RunnerPorts Default = new(8090, 8091);
}

public sealed record RunnerDependencies(RunnerOptions Options, RunnerPorts Ports, IDockerEngine Docker, IServerProbe Probe, TimeProvider Time);

public static class RunnerEndpoints
{
    private const long MaxArchiveBytes = 16 * 1024 * 1024;
    private const int ArchiveReadBufferBytes = 81920;
    private const int DefaultLogTail = 200;
    private const int MaxLogTail = 10_000;
    private const string BearerPrefix = "Bearer ";

    public static WebApplication BuildApp(string[] args, RunnerDependencies dependencies)
    {
        var builder = WebApplication.CreateBuilder(args);
        var (options, ports, docker, probe, time) = dependencies;

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var address = IPAddress.Parse(options.BindAddress);
            kestrel.Listen(address, ports.Ingress);
            kestrel.Listen(address, ports.Control);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(docker);
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(provider => new ServerRegistry(provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        builder.Services.AddSingleton<ServerNetworks>();
        builder.Services.AddSingleton<RouteTable>();
        builder.Services.AddSingleton<IProxyConfigProvider>(provider => provider.GetRequiredService<RouteTable>());
        builder.Services.AddSingleton<ReleaseBuilder>();
        builder.Services.AddSingleton<ReleaseRunner>();
        builder.Services.AddHostedService<Reconciler>();
        builder.Services.AddReverseProxy();
        builder.Services.AddDataProtection();
        builder.Services.Configure<KeyManagementOptions>(keys =>
        {
            keys.XmlRepository = new InMemoryXmlRepository();
            keys.XmlEncryptor = new NullXmlEncryptor();
        });

        var app = builder.Build();
        app.UseRouting();
        app.Use(RequireAccessAsync(ports, TokenHash(options.Token)));
        MapControl(app);
        app.MapReverseProxy(proxy => proxy.Use(RequireRouteSecretAsync));
        return app;
    }

    private static byte[] TokenHash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool Matches(string? presented, byte[] expectedHash) =>
        presented is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), expectedHash);

    private static Func<HttpContext, RequestDelegate, Task> RequireAccessAsync(RunnerPorts ports, byte[] tokenHash) =>
        async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            var isControlEndpoint = endpoint?.Metadata.GetMetadata<ControlEndpoint>() is not null;
            var isRouteEndpoint = endpoint?.Metadata.GetMetadata<RouteModel>() is not null;

            if (context.Connection.LocalPort == ports.Control)
            {
                if (!HasValidToken(context, tokenHash))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!isControlEndpoint)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await WithErrorMappingAsync(context, next);
                return;
            }

            if (!isRouteEndpoint)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        };

    private static bool HasValidToken(HttpContext context, byte[] tokenHash)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.Ordinal) && Matches(header[BearerPrefix.Length..], tokenHash);
    }

    private static async Task RequireRouteSecretAsync(HttpContext context, RequestDelegate next)
    {
        var metadata = context.GetReverseProxyFeature().Route.Config.Metadata;
        var expected = metadata?.GetValueOrDefault(RouteTable.SecretHashKey);
        var presented = context.Request.Headers[RouteSecret.Header];
        if (expected is null || presented.Count != 1 || !Matches(presented.ToString(), Convert.FromHexString(expected)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static async Task WithErrorMappingAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (RunnerRequestException failure) when (!context.Response.HasStarted)
        {
            await WriteProblemAsync(context, failure.Status, failure.Message);
        }
        catch (BuilderObjectRefusedException failure) when (!context.Response.HasStarted)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, failure.Message);
        }
        catch (Exception failure) when (failure is DockerApiException or HttpRequestException or IOException or SocketException or TimeoutException && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway, $"Docker did not complete the operation: {failure.Message}");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Detail = detail }, options: null, contentType: "application/problem+json");
    }

    private static void MapControl(WebApplication app)
    {
        var control = app.MapGroup("/v1").WithMetadata(new ControlEndpoint());

        control.MapGet("/health", HealthAsync);
        control.MapPut("/servers/{id:guid}", PutServer);
        control.MapDelete("/servers/{id:guid}", DeleteServerAsync);
        control.MapGet("/servers/{id:guid}", (Guid id, ServerRegistry registry) => Results.Ok(registry.Require(id).ToState()));
        control.MapPost("/servers/{id:guid}/releases/{number:int:min(1)}", BuildReleaseAsync);
        control.MapGet("/servers/{id:guid}/releases/{number:int:min(1)}", (Guid id, int number, ServerRegistry registry) =>
            Results.Ok(registry.Require(id).RequireRelease(number).ToStatus()));
        control.MapPost("/servers/{id:guid}/releases/{number:int:min(1)}/start", StartRelease);
        control.MapPost("/servers/{id:guid}/releases/{number:int:min(1)}/activate", async (Guid id, int number, ReleaseRunner runner, CancellationToken ct) =>
            Results.Ok(await runner.ActivateAsync(id, number, ct)));
        control.MapPost("/servers/{id:guid}/stop", async (Guid id, ReleaseRunner runner, CancellationToken ct) =>
        {
            await runner.StopAsync(id, ct);
            return Results.NoContent();
        });
        control.MapPost("/servers/{id:guid}/start", async (Guid id, ReleaseRunner runner, CancellationToken ct) =>
        {
            await runner.StartAsync(id, ct);
            return Results.NoContent();
        });
        control.MapGet("/servers/{id:guid}/logs", StreamLogsAsync);
    }

    private static async Task<IResult> HealthAsync(IDockerEngine docker, RunnerOptions options, CancellationToken ct)
    {
        var version = await docker.VersionAsync(ct);
        var egress = await IsEgressRunningAsync(docker, options, ct);
        var runnerVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Results.Ok(new RunnerHealth(runnerVersion, version, egress));
    }

    private static async Task<bool> IsEgressRunningAsync(IDockerEngine docker, RunnerOptions options, CancellationToken ct)
    {
        try
        {
            return await docker.RequireSystemContainerAsync(options.EgressContainer, ct);
        }
        catch (BuilderObjectRefusedException)
        {
            return false;
        }
    }

    private static IResult PutServer(Guid id, ServerSpec spec, ServerRegistry registry)
    {
        if (RejectionOf(spec) is { } reason)
        {
            return Results.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
        }

        var server = registry.GetOrAdd(id);
        lock (server.Sync)
        {
            server.Spec = spec;
        }

        return Results.NoContent();
    }

    private static string? RejectionOf(ServerSpec spec)
    {
        if (!Enum.IsDefined(spec.Network))
        {
            return "The network access mode is not known.";
        }

        if (string.IsNullOrWhiteSpace(spec.RouteSecret))
        {
            return "The route secret is required.";
        }

        if (spec.Limits is null)
        {
            return "The limits are required.";
        }

        try
        {
            ContainerTemplate.EnsureSafeLimits(spec.Limits);
            return null;
        }
        catch (ArgumentOutOfRangeException failure)
        {
            return failure.Message;
        }
    }

    private static async Task<IResult> DeleteServerAsync(Guid id, ReleaseRunner runner, CancellationToken ct)
    {
        await runner.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> BuildReleaseAsync(Guid id, int number, HttpContext context, ServerRegistry registry, ReleaseBuilder builder, CancellationToken ct)
    {
        registry.Require(id);
        if (await ReadBoundedAsync(context, ct) is not { } archive)
        {
            return Results.Problem(
                detail: $"The release archive is larger than the limit of {MaxArchiveBytes / (1024 * 1024)} MB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        builder.Begin(id, number, archive);
        return Results.Accepted();
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength > MaxArchiveBytes)
        {
            return null;
        }

        using var body = new MemoryStream();
        var buffer = new byte[ArchiveReadBufferBytes];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxArchiveBytes)
            {
                return null;
            }

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    private static IResult StartRelease(Guid id, int number, StartRelease request, ReleaseRunner runner)
    {
        if (request.Environment is null)
        {
            return Results.Problem(detail: "The environment is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        runner.BeginCandidate(id, number, request.Environment);
        return Results.Accepted();
    }

    private static async Task StreamLogsAsync(Guid id, int? tail, bool? follow, HttpContext context, ReleaseRunner runner, IDockerEngine docker, CancellationToken ct)
    {
        var container = await runner.RequireLogContainerAsync(id, ct);
        var lines = Math.Clamp(tail ?? DefaultLogTail, 1, MaxLogTail);
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(ct);
        try
        {
            await foreach (var chunk in docker.ReadLogsAsync(id, container, lines, follow == true, ct))
            {
                await context.Response.WriteAsync(chunk, ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private sealed class ControlEndpoint;
}
