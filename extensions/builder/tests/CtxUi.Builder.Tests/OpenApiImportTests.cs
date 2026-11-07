using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CtxUi.Builder.Components;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class OpenApiImportTests
{
    private const string Pets = """
                                {
                                  "openapi": "3.0.3",
                                  "info": { "title": "Pets", "version": "1" },
                                  "servers": [{ "url": "https://pets.example.com/api" }],
                                  "paths": {
                                    "/pets": {
                                      "get": { "operationId": "listPets", "summary": "List pets", "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } } } } },
                                      "post": { "responses": { "201": { "description": "Created" } } }
                                    },
                                    "/owners/{id}": {
                                      "get": { "operationId": "getOwner", "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }], "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Owner" } } } } } }
                                    }
                                  },
                                  "components": { "schemas": { "Pet": { "type": "object", "properties": { "owner": { "$ref": "#/components/schemas/Owner" } } }, "Owner": { "type": "object", "properties": { "name": { "type": "string" } } }, "Unused": { "type": "string" } } }
                                }
                                """;

    private static Stream Text(string text)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    private static FileStream Fixture(string file)
    {
        return File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return respond(request, cancellationToken);
        }
    }

    private sealed class EndlessStream(byte[] prefix, TimeSpan? pause = null) : Stream
    {
        private int _served;

        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (pause is { } delay)
                await Task.Delay(delay, cancellationToken);
            var count = Math.Min(buffer.Length, 65536);
            for (var index = 0; index < count; index++)
                buffer.Span[index] = _served + index < prefix.Length ? prefix[_served + index] : (byte)' ';
            _served += count;
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Import_selects_no_operations_by_default()
    {
        var api = await OpenApiImport.ParseAsync(Text(Pets), default);

        var selection = ImportSelection.Default(api, null);

        Assert.Empty(selection.Names);
        Assert.True(selection.UseBaseUrl);
        Assert.False(ImportSelection.Default(api, "https://already.example.com").UseBaseUrl);
        Assert.Equal(3, api.Operations.Count);
        Assert.Equal("https://pets.example.com/api", api.BaseUrl);
        Assert.Equal(("GET", "/pets", "List pets"),
            (api.Operations[0].Method, api.Operations[0].Path, api.Operations[0].Summary));
    }

    [Fact]
    public async Task Operation_without_operation_id_gets_a_stable_generated_name()
    {
        var first = await OpenApiImport.ParseAsync(Text(Pets), default);
        var second = await OpenApiImport.ParseAsync(Text(Pets), default);

        Assert.Equal("post_pets", first.Operations.Single(operation => operation.Method == "POST").Name);
        Assert.Equal(first.Operations.Select(operation => operation.Name),
            second.Operations.Select(operation => operation.Name));
        Assert.Contains("post_pets", OpenApiDraft.Read(first.OpenApiJson).Select(tool => tool.Name));
        Assert.Equal("post_pets",
            JsonNode.Parse(first.OpenApiJson)!["paths"]!["/pets"]!["post"]!["operationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Duplicate_operation_ids_are_made_unique()
    {
        const string document =
            """{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"operationId":"same","responses":{"200":{"description":"OK"}}}},"/b":{"get":{"operationId":"same","responses":{"200":{"description":"OK"}}}}}}""";

        var api = await OpenApiImport.ParseAsync(Text(document), default);

        Assert.Equal(["same", "same_2"], api.Operations.Select(operation => operation.Name));
    }

    [Fact]
    public async Task Selected_operations_keep_only_the_schemas_they_use()
    {
        var api = await OpenApiImport.ParseAsync(Text(Pets), default);

        var merged = OpenApiDraft.Merge(string.Empty, OpenApiImport.Select(api, new HashSet<string> { "listPets" }),
            new HashSet<string>());

        Assert.Equal(["listPets"], OpenApiDraft.Read(merged).Select(tool => tool.Name));
        var schemas = JsonNode.Parse(merged)!["components"]!["schemas"]!.AsObject();
        Assert.Equal(["Pet", "Owner"], schemas.Select(pair => pair.Key));
    }

    [Fact]
    public async Task Importing_a_name_that_is_already_taken_is_refused()
    {
        var api = await OpenApiImport.ParseAsync(Text(Pets), default);
        var subset = OpenApiImport.Select(api, new HashSet<string> { "listPets" });
        var merged = OpenApiDraft.Merge(string.Empty, subset, new HashSet<string>());

        Assert.Throws<BuilderException>(() => OpenApiDraft.Merge(merged, subset, new HashSet<string>()));
    }

    [Theory]
    [InlineData("broken.json")]
    [InlineData("empty.json")]
    public async Task Unsupported_document_is_refused_with_a_clear_message(string file)
    {
        await using var stream = Fixture(file);

        var failure = await Assert.ThrowsAsync<BuilderException>(() => OpenApiImport.ParseAsync(stream, default));

        Assert.Equal(file == "empty.json" ? BuilderText.ImportEmpty : BuilderText.ImportBroken, failure.Message);
    }

    [Fact]
    public async Task A_json_document_that_is_not_openapi_is_refused()
    {
        var failure =
            await Assert.ThrowsAsync<BuilderException>(() =>
                OpenApiImport.ParseAsync(Text("""{"name":"package"}"""), default));

        Assert.StartsWith(BuilderText.ImportUnsupported.Split("{0}")[0], failure.Message);
    }

    [Fact]
    public async Task Swagger_2_is_converted_to_openapi_3()
    {
        await using var stream = Fixture("swagger2.json");

        var api = await OpenApiImport.ParseAsync(stream, default);

        var operation = Assert.Single(api.Operations);
        Assert.Equal(("getPet", "GET", "/pets/{id}"), (operation.Name, operation.Method, operation.Path));
        Assert.StartsWith("3.", JsonNode.Parse(api.OpenApiJson)!["openapi"]!.GetValue<string>());
        Assert.Equal("https://pets.example.com/v2", api.BaseUrl);
        var tool = Assert.Single(OpenApiDraft.Read(api.OpenApiJson));
        Assert.Equal(ParameterPlace.Path, Assert.Single(tool.Parameters).Place);
    }

    [Fact]
    public async Task Document_without_operations_is_refused()
    {
        var failure = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.ParseAsync(Text("""{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{}}"""),
                default));

        Assert.Equal(BuilderText.ImportNoOperations, failure.Message);
    }

    [Fact]
    public async Task Fetch_resolves_a_relative_server_address_against_the_document()
    {
        const string document =
            """{"openapi":"3.0.3","info":{"title":"t","version":"1"},"servers":[{"url":"/api"}],"paths":{"/a":{"get":{"operationId":"a","responses":{"200":{"description":"OK"}}}}}}""";
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document) })));

        var api = await OpenApiImport.FetchAsync(new Uri("https://docs.example.com/openapi.json"), client, default);

        Assert.Equal("https://docs.example.com/api", api.BaseUrl);
    }

    [Fact]
    public async Task Fetch_stops_at_5_mb_and_20_seconds()
    {
        var oversized = new EndlessStream("{"u8.ToArray());
        using var big = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(oversized) })));

        var tooLarge = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/big.json"), big, default));

        Assert.Equal(BuilderText.ImportTooLarge, tooLarge.Message);
        Assert.InRange(oversized.Served, OpenApiImport.MaxDocumentBytes, OpenApiImport.MaxDocumentBytes + 131072);

        var slow = new EndlessStream("{"u8.ToArray(), TimeSpan.FromMilliseconds(200));
        using var trickle = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(slow) })));
        var timedOut = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/slow.json"), trickle, TimeSpan.FromSeconds(1),
                default));

        Assert.Equal(BuilderText.ImportTimedOut, timedOut.Message);
        Assert.Equal(TimeSpan.FromSeconds(20), OpenApiImport.FetchTimeout);
    }

    [Fact]
    public async Task Fetch_refuses_a_declared_length_over_the_limit_without_reading()
    {
        var content = new StreamContent(new EndlessStream("{"u8.ToArray()));
        content.Headers.ContentLength = OpenApiImport.MaxDocumentBytes + 1;
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));

        var failure = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/big.json"), client, default));

        Assert.Equal(BuilderText.ImportTooLarge, failure.Message);
    }

    [Fact]
    public async Task Fetch_reports_an_error_status_and_a_bad_address()
    {
        using var client =
            new HttpClient(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var missing = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/x.json"), client, default));
        var scheme = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("file:///etc/passwd"), client, default));

        Assert.Equal(string.Format(BuilderText.ImportFetchFailed, 404), missing.Message);
        Assert.Equal(BuilderText.ImportAddressInvalid, scheme.Message);
    }

    [Fact]
    public async Task Cancelling_the_caller_is_not_reported_as_a_timeout()
    {
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage();
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/x.json"), client, cancel.Token));
    }

    private const string Nullable31 =
        """{"openapi":"3.1.0","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"operationId":"a","parameters":[{"name":"q","in":"query","schema":{"anyOf":[{"type":"string"},{"type":"null"}]}}],"responses":{"200":{"description":"OK"}}}}}}""";

    [Fact]
    public async Task A_3_1_import_into_an_empty_draft_keeps_its_version()
    {
        var api = await OpenApiImport.ParseAsync(Text(Nullable31), default);

        var merged = OpenApiDraft.Merge(string.Empty, OpenApiImport.Select(api, new HashSet<string> { "a" }),
            new HashSet<string>());

        Assert.Equal("3.1.0", JsonNode.Parse(merged)!["openapi"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_draft_that_only_held_form_made_defaults_adopts_the_imported_version()
    {
        var api = await OpenApiImport.ParseAsync(Text(Nullable31), default);
        var empty = OpenApiDraft.Remove(
            OpenApiDraft.Upsert(string.Empty, new FormTool("x", "GET", "/x", null, [], null)), "x");

        var merged = OpenApiDraft.Merge(empty, OpenApiImport.Select(api, new HashSet<string> { "a" }),
            new HashSet<string>());

        Assert.Equal("3.1.0", JsonNode.Parse(merged)!["openapi"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mixing_openapi_versions_is_refused_with_a_message()
    {
        var api = await OpenApiImport.ParseAsync(Text(Nullable31), default);
        var existing = OpenApiDraft.Upsert(string.Empty, new FormTool("x", "GET", "/x", null, [], null));

        var failure = Assert.Throws<BuilderException>(() =>
            OpenApiDraft.Merge(existing, OpenApiImport.Select(api, new HashSet<string> { "a" }),
                new HashSet<string>()));

        Assert.Equal(string.Format(BuilderText.OpenApiVersionMismatch, "3.0.3", "3.1.0"), failure.Message);
    }

    [Fact]
    public async Task Imported_operation_ids_are_normalised_to_what_the_runtime_keeps()
    {
        var document =
            """{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"operationId":"get-forecast.v1","responses":{"200":{"description":"OK"}}}},"/b":{"get":{"operationId":"get_forecast_v1","responses":{"200":{"description":"OK"}}}},"/c":{"get":{"operationId":"a__b","responses":{"200":{"description":"OK"}}}},"/d":{"get":{"operationId":"LONG","responses":{"200":{"description":"OK"}}}}}}"""
                .Replace("LONG", new string('x', 70));

        var api = await OpenApiImport.ParseAsync(Text(document), default);

        Assert.Equal(["get_forecast_v1", "get_forecast_v1_2", "a_b", new string('x', 56)],
            api.Operations.Select(operation => operation.Name));
        Assert.Equal(api.Operations.Select(operation => operation.Name),
            OpenApiDraft.Read(api.OpenApiJson).Select(tool => tool.Name));
    }

    [Fact]
    public async Task Merge_reports_every_clashing_name_including_code_tools()
    {
        var api = await OpenApiImport.ParseAsync(Text(Pets), default);
        var subset = OpenApiImport.Select(api, new HashSet<string> { "listPets", "getOwner" });

        var failure = Assert.Throws<BuilderException>(() =>
            OpenApiDraft.Merge(string.Empty, subset, new HashSet<string> { "listPets", "getOwner" }));

        Assert.Equal(string.Format(BuilderText.ImportNameTaken, "listPets, getOwner"), failure.Message);
    }

    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw failure;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw failure;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task Fetch_maps_connection_failures_to_clear_messages(HttpRequestError error)
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException(error, "boom")));

        var failure = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/x.json"), client, default));

        Assert.Equal(
            error switch
            {
                HttpRequestError.NameResolutionError => BuilderText.ImportHostNotFound,
                HttpRequestError.SecureConnectionError => BuilderText.ImportTlsFailed,
                _ => BuilderText.ImportUnreachable
            }, failure.Message);
    }

    [Fact]
    public async Task Fetch_maps_a_broken_body_stream_to_a_clear_message()
    {
        var content = new StreamContent(new FailingStream(new IOException("reset")));
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));

        var failure = await Assert.ThrowsAsync<BuilderException>(() =>
            OpenApiImport.FetchAsync(new Uri("https://docs.example.com/x.json"), client, default));

        Assert.Equal(BuilderText.ImportInterrupted, failure.Message);
    }
}
