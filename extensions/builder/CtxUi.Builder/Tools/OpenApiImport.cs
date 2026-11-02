using System.Text.Json;
using System.Text.Json.Nodes;
using Enfilade;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace CtxUi.Builder.Tools;

public sealed record ImportedApi(string? BaseUrl, IReadOnlyList<ImportedOperation> Operations, string OpenApiJson);

public sealed record ImportedOperation(string Name, string Method, string Path, string? Summary);

public static class OpenApiImport
{
    public const long MaxDocumentBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private const int CopyBufferBytes = 81920;
    private const string ComponentsPrefix = "#/components/";

    public static async Task<ImportedApi> ParseAsync(Stream document, CancellationToken ct)
    {
        var imported = await ParseDocumentAsync(document, ct);
        return imported with { BaseUrl = imported.BaseUrl is { } address && !address.StartsWith('/') ? address : null };
    }

    private static async Task<ImportedApi> ParseDocumentAsync(Stream document, CancellationToken ct)
    {
        var bytes = await ReadLimitedAsync(document, ct);
        if (bytes.Length == 0 || bytes.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            throw new PanelException(BuilderText.ImportEmpty);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(bytes) as JsonObject ?? throw new PanelException(BuilderText.ImportBroken);
        }
        catch (JsonException)
        {
            throw new PanelException(BuilderText.ImportBroken);
        }

        root = await ValidatedAsync(root, bytes, ct);
        var operations = new List<ImportedOperation>();
        foreach (var (name, path, method, operation, _) in OpenApiDraft.Operations(root).ToList())
        {
            operation["operationId"] = name;
            operations.Add(new ImportedOperation(name, method.ToUpperInvariant(), path, SummaryOf(operation)));
        }

        if (operations.Count == 0)
            throw new PanelException(BuilderText.ImportNoOperations);
        return new ImportedApi(BaseUrlOf(root), operations, OpenApiDraft.Serialize(root));
    }

    public static Task<ImportedApi> FetchAsync(Uri address, HttpClient client, CancellationToken ct) =>
        FetchAsync(address, client, FetchTimeout, ct);

    public static async Task<ImportedApi> FetchAsync(Uri address, HttpClient client, TimeSpan timeout, CancellationToken ct)
    {
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
            throw new PanelException(BuilderText.ImportAddressInvalid);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode)
                throw new PanelException(string.Format(BuilderText.ImportFetchFailed, (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                throw new PanelException(BuilderText.ImportTooLarge);

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            var buffered = new MemoryStream(await ReadLimitedAsync(stream, limit.Token));
            var imported = await ParseDocumentAsync(buffered, limit.Token);
            return imported with { BaseUrl = Resolved(imported.BaseUrl, address) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PanelException(BuilderText.ImportTimedOut);
        }
        catch (HttpRequestException failure)
        {
            throw new PanelException(string.Format(BuilderText.ImportFetchFailed, failure.StatusCode is { } status ? (int)status : 0));
        }
    }

    public static string Select(ImportedApi api, IReadOnlySet<string> names)
    {
        var source = OpenApiDraft.Parse(api.OpenApiJson);
        var result = new JsonObject
        {
            ["openapi"] = source["openapi"]?.DeepClone() ?? "3.0.3",
            ["info"] = source["info"]?.DeepClone() ?? new JsonObject { ["title"] = "API", ["version"] = "1.0.0" },
        };
        var paths = new JsonObject();
        foreach (var entry in OpenApiDraft.Operations(source).Where(entry => names.Contains(entry.Name)))
        {
            var pathItem = paths[entry.Path] as JsonObject ?? (JsonObject)(paths[entry.Path] = new JsonObject());
            pathItem[entry.Method] = entry.Operation.DeepClone();
            foreach (var (key, value) in entry.PathItem.Where(pair => !OpenApiDraft.Methods.Contains(pair.Key) && pathItem[pair.Key] is null))
                pathItem[key] = value?.DeepClone();
        }

        result["paths"] = paths;
        if (source["components"] is JsonObject components)
        {
            var kept = Reachable(components, paths);
            if (kept.Count > 0)
                result["components"] = kept;
        }

        return OpenApiDraft.Serialize(result);
    }

    private static async Task<JsonObject> ValidatedAsync(JsonObject root, byte[] bytes, CancellationToken ct)
    {
        var isSwagger = root["swagger"] is not null;
        if (!isSwagger && root["openapi"] is null)
            throw new PanelException(string.Format(BuilderText.ImportUnsupported, BuilderText.ImportNotOpenApi));

        ReadResult result;
        try
        {
            result = await OpenApiDocument.LoadAsync(new MemoryStream(bytes), "json", new OpenApiReaderSettings(), ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new PanelException(string.Format(BuilderText.ImportUnsupported, failure.Message));
        }

        var errors = result.Diagnostic?.Errors ?? [];
        if (result.Document is null || errors.Count > 0)
            throw new PanelException(string.Format(BuilderText.ImportUnsupported, errors.FirstOrDefault()?.Message ?? BuilderText.ImportNotOpenApi));
        if (!isSwagger)
            return root;

        var converted = await result.Document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0, ct);
        return JsonNode.Parse(converted) as JsonObject ?? throw new PanelException(BuilderText.ImportBroken);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream source, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxDocumentBytes)
                throw new PanelException(BuilderText.ImportTooLarge);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? SummaryOf(JsonObject operation)
    {
        var text = operation["summary"]?.GetValueKind() == JsonValueKind.String ? operation["summary"]!.GetValue<string>()
            : operation["description"]?.GetValueKind() == JsonValueKind.String ? operation["description"]!.GetValue<string>() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? BaseUrlOf(JsonObject root)
    {
        if (root["servers"] is not JsonArray { Count: > 0 } servers || servers[0] is not JsonObject server || server["url"] is not JsonValue url)
            return null;
        var address = url.GetValue<string>();
        if (server["variables"] is JsonObject variables)
        {
            foreach (var (name, variable) in variables)
                address = address.Replace("{" + name + "}", variable?["default"]?.ToString() ?? string.Empty);
        }

        if (address.Contains('{') || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return IsRelative(address) ? address.Trim() : null;
        return uri.ToString().TrimEnd('/');
    }

    private static bool IsRelative(string address) => address.StartsWith('/') && !address.StartsWith("//") && !address.Contains('{');

    private static string? Resolved(string? baseUrl, Uri document)
    {
        if (baseUrl is null)
            return null;
        return baseUrl.StartsWith('/') ? new Uri(document, baseUrl).ToString().TrimEnd('/') : baseUrl;
    }

    private static JsonObject Reachable(JsonObject components, JsonObject paths)
    {
        var kept = new JsonObject();
        var queue = new Queue<JsonNode>();
        queue.Enqueue(paths);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var node))
        {
            foreach (var reference in References(node))
            {
                if (!reference.StartsWith(ComponentsPrefix, StringComparison.Ordinal) || !seen.Add(reference))
                    continue;
                var parts = reference[ComponentsPrefix.Length..].Split('/');
                if (parts.Length < 2 || components[parts[0]] is not JsonObject group || group[parts[1]] is not { } target)
                    continue;
                var bucket = kept[parts[0]] as JsonObject ?? (JsonObject)(kept[parts[0]] = new JsonObject());
                if (bucket[parts[1]] is null)
                    bucket[parts[1]] = target.DeepClone();
                queue.Enqueue(target);
            }
        }

        return kept;
    }

    private static IEnumerable<string> References(JsonNode node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (key, value) in members)
                {
                    if (key == "$ref" && value?.GetValueKind() == JsonValueKind.String)
                        yield return value.GetValue<string>();
                    else if (value is not null)
                        foreach (var nested in References(value))
                            yield return nested;
                }

                break;
            case JsonArray items:
                foreach (var item in items.OfType<JsonNode>())
                    foreach (var nested in References(item))
                        yield return nested;
                break;
        }
    }
}
