using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using CtxUi.Builder.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace CtxUi.Builder.Runner;

public sealed class RunnerClient(HttpClient http)
{
    private const int LogBufferChars = 4096;

    public Task<RunnerHealth> HealthAsync(CancellationToken ct) => ReadAsync<RunnerHealth>(HttpMethod.Get, "v1/health", null, ct);

    public Task PutServerAsync(Guid id, ServerSpec spec, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, ServerPath(id), JsonContent.Create(spec), ct);

    public Task DeleteServerAsync(Guid id, CancellationToken ct) => SendAsync(HttpMethod.Delete, ServerPath(id), null, ct);

    public Task<ServerState> ServerAsync(Guid id, CancellationToken ct) => ReadAsync<ServerState>(HttpMethod.Get, ServerPath(id), null, ct);

    public Task BuildReleaseAsync(Guid id, int number, Stream archive, CancellationToken ct)
    {
        var content = new StreamContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return SendAsync(HttpMethod.Post, ReleasePath(id, number), content, ct);
    }

    public Task<ReleaseStatus> ReleaseAsync(Guid id, int number, CancellationToken ct) =>
        ReadAsync<ReleaseStatus>(HttpMethod.Get, ReleasePath(id, number), null, ct);

    public Task StartReleaseAsync(Guid id, int number, StartRelease start, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{ReleasePath(id, number)}/start", JsonContent.Create(start), ct);

    public Task<ReleaseStatus> ActivateReleaseAsync(Guid id, int number, CancellationToken ct) =>
        ReadAsync<ReleaseStatus>(HttpMethod.Post, $"{ReleasePath(id, number)}/activate", null, ct);

    public Task StopServerAsync(Guid id, CancellationToken ct) => SendAsync(HttpMethod.Post, $"{ServerPath(id)}/stop", null, ct);

    public Task StartServerAsync(Guid id, CancellationToken ct) => SendAsync(HttpMethod.Post, $"{ServerPath(id)}/start", null, ct);

    public async IAsyncEnumerable<string> LogsAsync(Guid id, int? tail, bool follow, [EnumeratorCancellation] CancellationToken ct)
    {
        var query = new List<string>();
        if (tail is not null)
            query.Add($"tail={tail}");
        if (follow)
            query.Add("follow=true");
        var path = $"{ServerPath(id)}/logs{(query.Count == 0 ? "" : "?" + string.Join('&', query))}";

        using var response = await SendCoreAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[LogBufferChars];
        int read;
        while ((read = await ReadChunkAsync(reader, buffer, ct)) > 0)
            yield return new string(buffer, 0, read);
    }

    private static string ServerPath(Guid id) => $"v1/servers/{id}";

    private static string ReleasePath(Guid id, int number) => $"{ServerPath(id)}/releases/{number}";

    private static async Task<int> ReadChunkAsync(StreamReader reader, char[] buffer, CancellationToken ct)
    {
        try
        {
            return await reader.ReadAsync(buffer.AsMemory(), ct);
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException)
        {
            throw new RunnerException(null, failure.Message, failure);
        }
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, content, HttpCompletionOption.ResponseContentRead, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct)
                ?? throw new RunnerException(response.StatusCode, $"The runner returned an empty answer to {path}.");
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException or HttpRequestException or IOException or NotSupportedException)
        {
            throw new RunnerException(response.StatusCode, $"The runner returned an unreadable answer to {path}.", failure);
        }
    }

    private async Task SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendCoreAsync(method, path, content, HttpCompletionOption.ResponseContentRead, ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, HttpContent? content, HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException failure)
        {
            throw new RunnerException(null, failure.Message, failure);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var problem = await ProblemOfAsync(response, ct);
            var message = string.IsNullOrWhiteSpace(problem?.Detail) ? $"The runner answered {(int)response.StatusCode} {response.StatusCode}." : problem.Detail;
            throw new RunnerException(response.StatusCode, message, problem: problem?.Type);
        }
    }

    private static async Task<ProblemDetails?> ProblemOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        }
        catch (Exception failure) when (failure is System.Text.Json.JsonException or HttpRequestException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
