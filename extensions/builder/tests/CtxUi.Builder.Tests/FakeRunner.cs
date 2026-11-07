using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CtxUi.Builder.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace CtxUi.Builder.Tests;

public sealed partial class FakeRunner : HttpMessageHandler
{
    public sealed class Entry(int number, ReleaseState state)
    {
        public int Number { get; } = number;

        public ReleaseState State { get; set; } = state;

        public string Log { get; set; } = string.Empty;

        public string? LockFile { get; set; }

        public string? EnvironmentHash { get; set; }

        public string? Error { get; set; }
    }

    private readonly object _gate = new();

    public Dictionary<int, Entry> Releases { get; } = [];

    public Dictionary<int, byte[]> Archives { get; } = [];

    public List<string> Calls { get; } = [];

    public List<ServerSpec> Specs { get; } = [];

    public List<(int Number, StartRelease Start)> Starts { get; } = [];

    public HashSet<int> FailBuilds { get; } = [];

    public HashSet<int> FailStarts { get; } = [];

    public HashSet<int> FailActivations { get; } = [];

    public int? Active { get; set; }

    public bool HoldBuilds { get; set; }

    public bool Unreachable { get; set; }

    public bool DropSpecOnce { get; set; }

    public bool FailDeletes { get; set; }

    public HashSet<Guid> UnknownServers { get; } = [];

    public bool ServerDeleted { get; private set; }

    public bool KnowsSpec { get; private set; }

    public string[] CallsSnapshot()
    {
        lock (_gate)
        {
            return [.. Calls];
        }
    }

    public void FinishBuild(int number)
    {
        lock (_gate)
        {
            var entry = Releases[number];
            entry.State = ReleaseState.Built;
            entry.LockFile = $"lock-{number}";
            entry.EnvironmentHash = "environment";
            entry.Log += $"built {number}\n";
        }
    }

    public void Restart(bool loseMarker = false)
    {
        lock (_gate)
        {
            KnowsSpec = false;
            foreach (var entry in Releases.Values.ToList())
            {
                entry.LockFile = null;
                entry.Log = string.Empty;
                if (entry.State == ReleaseState.Building)
                {
                    entry.State = ReleaseState.Failed;
                    entry.Error = "The build was interrupted by a restart of the runner.";
                }
                else if (entry.State == ReleaseState.Built)
                {
                    Releases.Remove(entry.Number);
                }
                else if (loseMarker && entry.State == ReleaseState.Active)
                {
                    entry.State = ReleaseState.Candidate;
                }
            }

            if (loseMarker)
                Active = null;
        }
    }

    public void Forget(int number)
    {
        lock (_gate)
        {
            Releases.Remove(number);
        }
    }

    public void SeedBuilt(int number, ReleaseState state = ReleaseState.Retired)
    {
        lock (_gate)
        {
            Releases[number] = new Entry(number, state)
            { EnvironmentHash = "environment", LockFile = $"lock-{number}" };
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Unreachable)
            throw new HttpRequestException("connection refused");

        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_gate)
        {
            return Handle(request.Method, path, body);
        }
    }

    private HttpResponseMessage Handle(HttpMethod method, string path, byte[] body)
    {
        var match = RoutePattern().Match(path);
        if (!match.Success)
            return Problem(HttpStatusCode.NotFound, "unknown path");

        if (match.Groups["server"].Success && UnknownServers.Contains(Guid.Parse(match.Groups["server"].Value)) &&
            method == HttpMethod.Get && !match.Groups["number"].Success)
            return Problem(HttpStatusCode.NotFound, "unknown server");

        var release = match.Groups["number"].Success ? int.Parse(match.Groups["number"].Value) : (int?)null;
        var action = match.Groups["action"].Value;
        Calls.Add(
            $"{method} {(release is null ? "server" : "release " + release)}{(action.Length == 0 ? "" : " " + action)}");

        if (release is null)
            return HandleServer(method, action, body);

        return (method.Method, action) switch
        {
            ("POST", "") => Build(release.Value, body),
            ("GET", "") => Releases.TryGetValue(release.Value, out var entry)
                ? Json(Status(entry))
                : Problem(HttpStatusCode.NotFound, "unknown release"),
            ("POST", "start") => Start(release.Value, body),
            ("POST", "activate") => Activate(release.Value),
            _ => Problem(HttpStatusCode.NotFound, "unknown action")
        };
    }

    private HttpResponseMessage HandleServer(HttpMethod method, string action, byte[] body)
    {
        if (method == HttpMethod.Put)
        {
            Specs.Add(System.Text.Json.JsonSerializer.Deserialize<ServerSpec>(body,
                System.Text.Json.JsonSerializerOptions.Web)!);
            KnowsSpec = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Delete)
        {
            if (FailDeletes)
                return Problem(HttpStatusCode.InternalServerError, "The runner could not remove the server.");
            ServerDeleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == HttpMethod.Get && action.Length == 0)
            return Json(new ServerState(Guid.Empty, Active,
                [.. Releases.Values.OrderBy(entry => entry.Number).Select(Status)]));

        return Problem(HttpStatusCode.NotFound, "unknown action");
    }

    private HttpResponseMessage Build(int number, byte[] archive)
    {
        if (DropSpecOnce)
        {
            DropSpecOnce = false;
            KnowsSpec = false;
        }

        if (!KnowsSpec)
            return Problem(HttpStatusCode.Conflict, "The specification of server is not known.",
                RunnerProblems.SpecRequired);
        if (Releases.Values.Any(entry => entry.State == ReleaseState.Building))
            return Problem(HttpStatusCode.Conflict,
                "A release of this server is still building. Only one build runs at a time.");
        if (Releases.TryGetValue(number, out var existing) && existing.State != ReleaseState.Failed)
            return Problem(HttpStatusCode.Conflict, $"Release {number} already exists with state {existing.State}.");

        Archives[number] = archive;
        var entry = new Entry(number, ReleaseState.Building);
        Releases[number] = entry;
        if (HoldBuilds)
            return new HttpResponseMessage(HttpStatusCode.Accepted);

        if (FailBuilds.Contains(number))
        {
            entry.State = ReleaseState.Failed;
            entry.Error = "The dependency install failed.";
            entry.Log = "ERROR: no matching distribution\n";
        }
        else
        {
            entry.State = ReleaseState.Built;
            entry.LockFile = $"lock-{number}";
            entry.EnvironmentHash = "environment";
            entry.Log = $"built {number}\n";
        }

        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    private HttpResponseMessage Start(int number, byte[] body)
    {
        if (!KnowsSpec)
            return Problem(HttpStatusCode.Conflict,
                "The specification of server is not known. Send it with PUT before a start.");
        if (!Releases.TryGetValue(number, out var entry))
            return Problem(HttpStatusCode.NotFound, "unknown release");

        Starts.Add((number,
            System.Text.Json.JsonSerializer.Deserialize<StartRelease>(body,
                System.Text.Json.JsonSerializerOptions.Web)!));
        if (FailStarts.Contains(number))
        {
            entry.State = ReleaseState.Failed;
            entry.Error = "The server did not answer its health check within 60 seconds.";
        }
        else
        {
            entry.State = ReleaseState.Candidate;
            entry.Error = null;
        }

        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    private HttpResponseMessage Activate(int number)
    {
        if (!Releases.TryGetValue(number, out var entry) || entry.State != ReleaseState.Candidate ||
            FailActivations.Contains(number))
            return Problem(HttpStatusCode.Conflict, $"Release {number} is not a healthy candidate.");

        if (Active is { } previous && previous != number && Releases.TryGetValue(previous, out var old))
            old.State = ReleaseState.Retired;
        Active = number;
        entry.State = ReleaseState.Active;
        return Json(Status(entry));
    }

    private static ReleaseStatus Status(Entry entry)
    {
        return new ReleaseStatus(entry.Number, entry.State, entry.Log, entry.LockFile, entry.EnvironmentHash,
            entry.Error);
    }

    private static HttpResponseMessage Json<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string detail, string? type = null)
    {
        return new HttpResponseMessage(status)
        { Content = JsonContent.Create(new ProblemDetails { Status = (int)status, Detail = detail, Type = type }) };
    }

    [GeneratedRegex(@"^/v1/servers/(?<server>[0-9a-f-]{36})(/releases/(?<number>\d+))?(/(?<action>[a-z]+))?$")]
    private static partial Regex RoutePattern();
}
